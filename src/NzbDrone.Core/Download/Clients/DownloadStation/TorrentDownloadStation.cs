using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.DownloadStation.Proxies;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.DownloadStation
{
    public class TorrentDownloadStation : TorrentClientBase<DownloadStationSettings>
    {
        protected readonly IDownloadStationInfoProxy _dsInfoProxy;
        protected readonly IDownloadStationTaskProxy _dsTaskProxy;
        protected readonly ISharedFolderResolver _sharedFolderResolver;
        protected readonly ISerialNumberProvider _serialNumberProvider;
        protected readonly IFileStationProxy _fileStationProxy;

        public TorrentDownloadStation(ISharedFolderResolver sharedFolderResolver,
                                      ISerialNumberProvider serialNumberProvider,
                                      IFileStationProxy fileStationProxy,
                                      IDownloadStationInfoProxy dsInfoProxy,
                                      IDownloadStationTaskProxy dsTaskProxy,
                                      ITorrentFileInfoReader torrentFileInfoReader,
                                      IHttpClient httpClient,
                                      IConfigService configService,
                                      IDiskProvider diskProvider,
                                      IRemotePathMappingService remotePathMappingService,
                                      Logger logger)
            : base(torrentFileInfoReader, httpClient, configService, diskProvider, remotePathMappingService, logger)
        {
            _dsInfoProxy = dsInfoProxy;
            _dsTaskProxy = dsTaskProxy;
            _fileStationProxy = fileStationProxy;
            _sharedFolderResolver = sharedFolderResolver;
            _serialNumberProvider = serialNumberProvider;
        }

        public override string Name => "Download Station";

        protected IEnumerable<DownloadStationTask> GetTasks()
        {
            return _dsTaskProxy.GetTasks(Settings).Where(v => v.Type.ToLower() == DownloadStationTaskType.BT.ToString().ToLower());
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var torrents = GetTasks();
            var serialNumber = _serialNumberProvider.GetSerialNumber(Settings);

            var items = new List<DownloadClientItem>();

            foreach (var torrent in torrents)
            {
                var outputPath = new OsPath($"/{torrent.Additional.Detail["destination"]}");

                if (Settings.TvDirectory.IsNotNullOrWhiteSpace())
                {
                    if (!new OsPath($"/{Settings.TvDirectory}").Contains(outputPath))
                    {
                        continue;
                    }
                }
                else if (Settings.MusicCategory.IsNotNullOrWhiteSpace())
                {
                    var directories = outputPath.FullPath.Split('\\', '/');
                    if (!directories.Contains(Settings.MusicCategory))
                    {
                        continue;
                    }
                }

                var item = new DownloadClientItem()
                {
                    Category = Settings.MusicCategory,
                    DownloadClient = Definition.Name,
                    DownloadId = CreateDownloadId(torrent.Id, serialNumber),
                    Title = torrent.Title,
                    TotalSize = torrent.Size,
                    RemainingSize = GetRemainingSize(torrent),
                    RemainingTime = GetRemainingTime(torrent),
                    Status = GetStatus(torrent),
                    Message = GetMessage(torrent),
                    CanMoveFiles = IsCompleted(torrent),
                    CanBeRemoved = IsFinished(torrent)
                };

                if (item.Status == DownloadItemStatus.Completed || item.Status == DownloadItemStatus.Failed)
                {
                    item.OutputPath = GetOutputPath(outputPath, torrent, serialNumber);
                }

                items.Add(item);
            }

            return items;
        }

        public override DownloadClientInfo GetStatus()
        {
            try
            {
                var path = GetDownloadDirectory();

                return new DownloadClientInfo
                {
                    IsLocalhost = Settings.Host == "127.0.0.1" || Settings.Host == "localhost",
                    OutputRootFolders = new List<OsPath> { _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(path)) }
                };
            }
            catch (DownloadClientException e)
            {
                _logger.Debug(e, "Failed to get config from Download Station");

                throw e;
            }
        }

        public override void RemoveItem(string downloadId, bool deleteData)
        {
            if (deleteData)
            {
                DeleteItemData(downloadId);
            }

            _dsTaskProxy.RemoveTask(ParseDownloadId(downloadId), Settings);
            _logger.Debug("{0} removed correctly", downloadId);
        }

        protected OsPath GetOutputPath(OsPath outputPath, DownloadStationTask torrent, string serialNumber)
        {
            var fullPath = _sharedFolderResolver.RemapToFullPath(outputPath, Settings, serialNumber);

            var remotePath = _remotePathMappingService.RemapRemoteToLocal(Settings.Host, fullPath);

            var finalPath = remotePath + torrent.Title;

            return finalPath;
        }

        protected override string AddFromMagnetLink(RemoteAlbum remoteAlbum, string hash, string magnetLink)
        {
            var hashedSerialNumber = _serialNumberProvider.GetSerialNumber(Settings);

            _dsTaskProxy.AddTaskFromUrl(magnetLink, GetDownloadDirectory(), Settings);

            var item = GetTasks().SingleOrDefault(t => t.Additional.Detail["uri"] == magnetLink);

            if (item != null)
            {
                _logger.Debug("{0} added correctly", remoteAlbum);
                return CreateDownloadId(item.Id, hashedSerialNumber);
            }

            _logger.Debug("No such task {0} in Download Station", magnetLink);

            throw new DownloadClientException("Failed to add magnet task to Download Station");
        }

        protected override string AddFromTorrentFile(RemoteAlbum remoteAlbum, string hash, string filename, byte[] fileContent)
        {
            var hashedSerialNumber = _serialNumberProvider.GetSerialNumber(Settings);

            _dsTaskProxy.AddTaskFromData(fileContent, filename, GetDownloadDirectory(), Settings);

            var items = GetTasks().Where(t => t.Additional.Detail["uri"] == Path.GetFileNameWithoutExtension(filename));

            var item = items.SingleOrDefault();

            if (item != null)
            {
                _logger.Debug("{0} added correctly", remoteAlbum);
                return CreateDownloadId(item.Id, hashedSerialNumber);
            }

            _logger.Debug("No such task {0} in Download Station", filename);

            throw new DownloadClientException("Failed to add torrent task to Download Station");
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            if (failures.Any()) return;
            failures.AddIfNotNull(TestOutputPath());
            failures.AddIfNotNull(TestGetTorrents());
        }

        protected bool IsFinished(DownloadStationTask torrent)
        {
            return torrent.Status == DownloadStationTaskStatus.Finished;
        }

        protected bool IsCompleted(DownloadStationTask torrent)
        {
            return torrent.Status == DownloadStationTaskStatus.Seeding || IsFinished(torrent) ||  (torrent.Status == DownloadStationTaskStatus.Waiting && torrent.Size != 0 && GetRemainingSize(torrent) <= 0);
        }

    protected string GetMessage(DownloadStationTask torrent)
        {
            if (torrent.StatusExtra != null)
            {
                if (torrent.Status == DownloadStationTaskStatus.Extracting)
                {
                    return $"Extracting: {int.Parse(torrent.StatusExtra["unzip_progress"])}%";
                }

                if (torrent.Status == DownloadStationTaskStatus.Error)
                {
                    return torrent.StatusExtra["error_detail"];
                }
            }

            return null;
        }

        protected DownloadItemStatus GetStatus(DownloadStationTask torrent)
        {
            switch (torrent.Status)
            {
                case DownloadStationTaskStatus.Unknown:
                case DownloadStationTaskStatus.Waiting:
                case DownloadStationTaskStatus.FilehostingWaiting:
                    return torrent.Size == 0 || GetRemainingSize(torrent) > 0 ? DownloadItemStatus.Queued : DownloadItemStatus.Completed;
                case DownloadStationTaskStatus.Paused:
                    return DownloadItemStatus.Paused;
                case DownloadStationTaskStatus.Finished:
                case DownloadStationTaskStatus.Seeding:
                    return DownloadItemStatus.Completed;
                case DownloadStationTaskStatus.Error:
                    return DownloadItemStatus.Failed;
            }

            return DownloadItemStatus.Downloading;
        }

        protected long GetRemainingSize(DownloadStationTask torrent)
        {
            var downloadedString = torrent.Additional.Transfer["size_downloaded"];
            long downloadedSize;

            if (downloadedString.IsNullOrWhiteSpace() || !long.TryParse(downloadedString, out downloadedSize))
            {
                _logger.Debug("Torrent {0} has invalid size_downloaded: {1}", torrent.Title, downloadedString);
                downloadedSize = 0;
            }

            return torrent.Size - Math.Max(0, downloadedSize);
        }

        protected TimeSpan? GetRemainingTime(DownloadStationTask torrent)
        {
            var speedString = torrent.Additional.Transfer["speed_download"];
            long downloadSpeed;

            if (speedString.IsNullOrWhiteSpace() || !long.TryParse(speedString, out downloadSpeed))
            {
                _logger.Debug("Torrent {0} has invalid speed_download: {1}", torrent.Title, speedString);
                downloadSpeed = 0;
            }

            if (downloadSpeed <= 0)
            {
                return null;
            }

            var remainingSize = GetRemainingSize(torrent);

            return TimeSpan.FromSeconds(remainingSize / downloadSpeed);
        }

        protected ValidationFailure TestOutputPath()
        {
            try
            {
                var downloadDir = GetDefaultDir();

                if (downloadDir == null)
                {
                    return new NzbDroneValidationFailure(nameof(Settings.TvDirectory), "No default destination")
                    {
                        DetailedDescription = $"You must login into your Diskstation as {Settings.Username} and manually set it up into DownloadStation settings under BT/HTTP/FTP/NZB -> Location."
                    };
                }

                downloadDir = GetDownloadDirectory();

                if (downloadDir != null)
                {
                    var sharedFolder = downloadDir.Split('\\', '/')[0];
                    var fieldName = Settings.TvDirectory.IsNotNullOrWhiteSpace() ? nameof(Settings.TvDirectory) : nameof(Settings.MusicCategory);

                    var folderInfo = _fileStationProxy.GetInfoFileOrDirectory($"/{downloadDir}", Settings);

                    if (folderInfo.Additional == null)
                    {
                        return new NzbDroneValidationFailure(fieldName, $"Shared folder does not exist")
                        {
                            DetailedDescription = $"The Diskstation does not have a Shared Folder with the name '{sharedFolder}', are you sure you specified it correctly?"
                        };
                    }

                    if (!folderInfo.IsDir)
                    {
                        return new NzbDroneValidationFailure(fieldName, $"Folder does not exist")
                        {
                            DetailedDescription = $"The folder '{downloadDir}' does not exist, it must be created manually inside the Shared Folder '{sharedFolder}'."
                        };
                    }
                }

                return null;
            }
            catch (DownloadClientAuthenticationException ex) // User could not have permission to access to downloadstation
            {
                _logger.Error(ex, ex.Message);
                return new NzbDroneValidationFailure(string.Empty, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error testing Torrent Download Station");
                return new NzbDroneValidationFailure(string.Empty, $"Unknown exception: {ex.Message}");
            }
        }

        protected ValidationFailure TestConnection()
        {
            try
            {
                return ValidateVersion();
            }
            catch (DownloadClientAuthenticationException ex)
            {
                _logger.Error(ex, ex.Message);
                return new NzbDroneValidationFailure("Username", "Authentication failure")
                {
                    DetailedDescription = $"Please verify your username and password. Also verify if the host running Lidarr isn't blocked from accessing {Name} by WhiteList limitations in the {Name} configuration."
                };
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Unable to connect to Torrent Download Station");

                if (ex.Status == WebExceptionStatus.ConnectFailure)
                {
                    return new NzbDroneValidationFailure("Host", "Unable to connect")
                    {
                        DetailedDescription = "Please verify the hostname and port."
                    };
                }
                return new NzbDroneValidationFailure(string.Empty, $"Unknown exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error testing Torrent Download Station");
                return new NzbDroneValidationFailure(string.Empty, $"Unknown exception: {ex.Message}");
            }
        }

        protected ValidationFailure ValidateVersion()
        {
            var info = _dsTaskProxy.GetApiInfo(Settings);

            _logger.Debug("Download Station api version information: Min {0} - Max {1}", info.MinVersion, info.MaxVersion);

            if (info.MinVersion > 2 || info.MaxVersion < 2)
            {
                return new ValidationFailure(string.Empty, $"Download Station API version not supported, should be at least 2. It supports from {info.MinVersion} to {info.MaxVersion}");
            }

            return null;
        }

        protected ValidationFailure TestGetTorrents()
        {
            try
            {
                GetItems();
                return null;
            }
            catch (Exception ex)
            {
                return new NzbDroneValidationFailure(string.Empty, $"Failed to get the list of torrents: {ex.Message}");
            }
        }

        protected string ParseDownloadId(string id)
        {
            return id.Split(':')[1];
        }

        protected string CreateDownloadId(string id, string hashedSerialNumber)
        {
            return $"{hashedSerialNumber}:{id}";
        }

        protected string GetDefaultDir()
        {
            var config = _dsInfoProxy.GetConfig(Settings);

            var path = config["default_destination"] as string;

            return path;
        }

        protected string GetDownloadDirectory()
        {
            if (Settings.TvDirectory.IsNotNullOrWhiteSpace())
            {
                return Settings.TvDirectory.TrimStart('/');
            }
            else if (Settings.MusicCategory.IsNotNullOrWhiteSpace())
            {
                var destDir = GetDefaultDir();

                return $"{destDir.TrimEnd('/')}/{Settings.MusicCategory}";
            }

            return null;
        }
    }
}
