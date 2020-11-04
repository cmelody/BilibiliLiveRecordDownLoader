﻿using BilibiliApi.Clients;
using BilibiliLiveRecordDownLoader.FlvProcessor.Interfaces;
using BilibiliLiveRecordDownLoader.Http.DownLoaders;
using Splat;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace BilibiliLiveRecordDownLoader.Services
{
    public class LiveRecordDownloadTask
    {
        public ushort ThreadsCount { get; set; } = 8;

        private readonly string _id;
        private readonly DateTime _startTime;
        private readonly DownloadTaskPool _parent;
        private readonly string _root;
        private string RecordPath => Path.Combine(_root, _id);

        private readonly Subject<double> _progress = new Subject<double>();
        public IObservable<double> ProgressUpdated => _progress.AsObservable();

        private CancellationTokenSource _cts;

        public bool IsDownloading => _cts != null && !_cts.IsCancellationRequested;

        public LiveRecordDownloadTask(string id, DateTime startTime, DownloadTaskPool parent, string path)
        {
            _id = id;
            _startTime = startTime;
            _parent = parent;
            _root = path;
        }

        /// <summary>
        /// 开始或停止下载
        /// </summary>
        public async Task StartOrStopAsync()
        {
            if (IsDownloading)
            {
                Stop();
            }
            else
            {
                await StartAsync();
            }
        }

        private async Task StartAsync()
        {
            try
            {
                _cts = new CancellationTokenSource();

                using var client = new BililiveApiClient();
                var message = await client.GetLiveRecordUrl(_id, _cts.Token);

                var list = message?.data?.list;
                if (list == null)
                {
                    return;
                }

                var l = list.Where(x => !string.IsNullOrEmpty(x.url) || !string.IsNullOrEmpty(x.backup_url))
                        .Select(x => string.IsNullOrEmpty(x.url) ? x.backup_url : x.url).ToArray();

                _progress.OnNext(0.0);

                for (var i = 0; i < l.Length; ++i)
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        throw new TaskCanceledException(@"下载已取消！");
                    }

                    var url = l[i];
                    var outfile = Path.Combine(RecordPath, $@"{i + 1}.flv");
                    if (File.Exists(outfile))
                    {
                        _progress.OnNext((1.0 + i) / l.Length);
                        continue;
                    }

                    await using var downloader = Locator.Current.GetService<IDownloader>();
                    downloader.Target = new Uri(url);
                    downloader.Threads = ThreadsCount;
                    downloader.OutFileName = outfile;
                    downloader.TempDir = RecordPath;

                    using var d2 = Observable.Interval(TimeSpan.FromSeconds(0.2))
                        .DistinctUntilChanged()
                        .Subscribe(_ =>
                        {
                            // ReSharper disable once AccessToModifiedClosure
                            // ReSharper disable once AccessToDisposedClosure
                            _progress.OnNext((downloader.Progress + i) / l.Length);
                        });

                    await downloader.DownloadAsync(_cts.Token);
                }

                //Merge flv

                var filename = _startTime == default ? _id : $@"{_startTime:yyyyMMdd_HHmmss}";
                var mergeFlv = Path.Combine(_root, $@"{filename}.flv");
                if (l.Length > 1)
                {
                    await using var flv = Locator.Current.GetService<IFlvMerger>();
                    flv.AddRange(Enumerable.Range(1, l.Length).Select(i => Path.Combine(RecordPath, $@"{i}.flv")));

                    using var d2 = Observable.Interval(TimeSpan.FromSeconds(0.1))
                        .DistinctUntilChanged()
                        .Subscribe(_ =>
                        {
                            // ReSharper disable once AccessToDisposedClosure
                            _progress.OnNext(flv.Progress);
                        });

                    await flv.MergeAsync(mergeFlv, _cts.Token);
                    Utils.Utils.DeleteFiles(RecordPath);
                }
                else if (l.Length == 1)
                {
                    var inputFile = Path.Combine(RecordPath, @"1.flv");
                    File.Move(inputFile, mergeFlv, true);
                    Utils.Utils.DeleteFiles(RecordPath);
                }

                _progress.OnNext(1.0);
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                _progress.OnError(ex);
            }
            finally
            {
                _parent.Remove(_id);
                _progress.OnCompleted();
                _cts?.Dispose();
                _cts = null;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
        }
    }
}
