using BilibiliApi.Clients;
using BilibiliLiveRecordDownLoader.FlvProcessor.Interfaces;
using BilibiliLiveRecordDownLoader.Http.Clients;
using Microsoft.Extensions.Logging;
using Splat;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace BilibiliLiveRecordDownLoader.Models.TaskViewModels
{
	public class LiveRecordDownloadTaskViewModel : TaskViewModel
	{
		private readonly ILogger _logger;
		private readonly BililiveApiClient _apiClient;
		private readonly Config _config;

		private readonly CancellationTokenSource _cts = new();
		private readonly LiveRecordViewModel _liveRecord;
		private readonly string _path;
		private readonly string _recordPath;
		private readonly ushort _threadsCount;

		public LiveRecordDownloadTaskViewModel(LiveRecordViewModel liveRecord, string path, ushort threadsCount)
		{
			_logger = Locator.Current.GetService<ILogger<LiveRecordDownloadTaskViewModel>>();
			_apiClient = Locator.Current.GetService<BililiveApiClient>();
			_config = Locator.Current.GetService<Config>();
			_liveRecord = liveRecord;
			_path = path;
			_threadsCount = threadsCount;

			Status = @"未开始";
			Description = $@"{liveRecord.Rid}";
			_recordPath = Path.Combine(_path, liveRecord.Rid!);
		}

		public override async ValueTask StartAsync()
		{
			try
			{
				_cts.Token.ThrowIfCancellationRequested();

				Status = @"正在获取回放地址";
				var message = await _apiClient.GetLiveRecordUrlAsync(_liveRecord.Rid!, _cts.Token);

				var list = message?.data?.list;
				if (list is null)
				{
					return;
				}

				var l = list.Where(x => !string.IsNullOrEmpty(x.url) || !string.IsNullOrEmpty(x.backup_url))
						.Select(x => string.IsNullOrEmpty(x.url) ? x.backup_url : x.url)
						.ToArray();

				if (list.Length != l.Length)
				{
					throw new Exception(@"获取的分段地址不完整！");
				}

				Status = @"开始下载...";
				Progress = 0.0;

				for (var i = 0; i < l.Length; ++i)
				{
					_cts.Token.ThrowIfCancellationRequested();

					var url = l[i]!;
					var outfile = Path.Combine(_recordPath, $@"{i + 1}.flv");
					if (File.Exists(outfile))
					{
						Progress = (1.0 + i) / l.Length;
						continue;
					}

					await using var downloader = new MultiThreadedDownloader(_logger, _config.Cookie, _config.UserAgent)
					{
						Target = new(url),
						Threads = _threadsCount,
						OutFileName = outfile,
						TempDir = _recordPath
					};

					using var ds = downloader.Status.DistinctUntilChanged().Subscribe(s =>
							// ReSharper disable once AccessToModifiedClosure
							Status = $@"[{i + 1}/{l.Length}] {s}");

					using var d = downloader.CurrentSpeed.DistinctUntilChanged().Subscribe(speed =>
							Speed = $@"{Utils.Utils.CountSize(Convert.ToInt64(speed))}/s");

					using var dp = Observable.Interval(TimeSpan.FromSeconds(0.2))
							.DistinctUntilChanged()
							.Subscribe(_ =>
								// ReSharper disable once AccessToModifiedClosure
								// ReSharper disable once AccessToDisposedClosure
								Progress = (downloader.Progress + i) / l.Length);

					await downloader.DownloadAsync(_cts.Token);
				}

				Progress = 1.0;

				//Merge flv

				Status = @"正在合并分段...";
				Progress = 0.0;

				var filename = _liveRecord.StartTime == default
						? _liveRecord.Rid
						: $@"{_liveRecord.StartTime:yyyyMMdd_HHmmss}";
				var mergeFlv = Path.Combine(_path, $@"{filename}.flv");
				if (l.Length > 1)
				{
					await using var flv = Locator.Current.GetService<IFlvMerger>();
					flv.AddRange(Enumerable.Range(1, l.Length).Select(i => Path.Combine(_recordPath, $@"{i}.flv")));

					using var ds = flv.Status.DistinctUntilChanged().Subscribe(s => Status = s);

					using var d = flv.CurrentSpeed.DistinctUntilChanged().Subscribe(speed => Speed = $@"{Utils.Utils.CountSize(Convert.ToInt64(speed))}/s");

					using var dp = Observable.Interval(TimeSpan.FromSeconds(0.1))
							.DistinctUntilChanged()
							.Subscribe(_ =>
								// ReSharper disable once AccessToDisposedClosure
								Progress = flv.Progress);

					await flv.MergeAsync(mergeFlv, _cts.Token);
					Utils.Utils.DeleteFiles(_recordPath);
				}
				else if (l.Length == 1)
				{
					Status = @"只有一段，进行移动...";
					var inputFile = Path.Combine(_recordPath, @"1.flv");
					File.Move(inputFile, mergeFlv, true);
					Utils.Utils.DeleteFiles(_recordPath);
				}

				Status = @"完成";
				Progress = 1.0;
			}
			catch (OperationCanceledException)
			{
				_logger.LogInformation($@"下载已取消：{_liveRecord.Rid}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"下载直播回放出错");
			}
			finally
			{
				Speed = string.Empty;
			}
		}

		public override void Stop()
		{
			_cts.Cancel();
		}
	}
}
