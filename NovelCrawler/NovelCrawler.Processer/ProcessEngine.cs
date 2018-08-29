﻿using NovelCrawler.Infrastructure;
using NovelCrawler.Infrastructure.Router;
using NovelCrawler.Models;
using NovelCrawler.Processer.Models;
using NovelCrawler.Repository.IRepository;
using NovelCrawler.Repository.Repository;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace NovelCrawler.Processer
{
    public class ProcessEngine
    {
        private static readonly object _obj = new object();
        private static ProcessEngine _instance;
        private ProcessEngineOptions _options;

        private INovelInfoRepository _novelInfoRepository;
        private INovelIndexRepository _novelIndexRepository;
        private INovelChapterRepository _novelChapterRepository;

        private ProcessEngine(ProcessEngineOptions options)
        {
            _options = options;

            _novelInfoRepository = new NovelInfoRepository();
            _novelIndexRepository = new NovelIndexRepository();
            _novelChapterRepository = new NovelChapterRepository();
        }

        public static ProcessEngine Create(ProcessEngineOptions options = null)
        {
            if (_instance == null)
            {
                lock (_obj)
                {
                    if (_instance == null)
                    {
                        _instance = new ProcessEngine(options ?? new ProcessEngineOptions());
                    }
                }
            }
            return _instance;
        }

        public void Start()
        {
            Process();
        }

        public void Stop()
        {

        }


        private async Task Process()
        {
            var rule = XmlHelper.XmlDeserializeFromFile<RuleModel>("testRule.xml", Encoding.UTF8);
            var spider = new Spider(null, rule);

            //获取更新列表
            var novelKeys = await spider.GetUpdateList();

            //并行抓取
            Parallel.ForEach(novelKeys, async (novelKey, loopState) =>
             {
                 try
                 {
                     //获取小说详情
                     var info = await spider.GetNovelInfo(novelKey);
                     //判断是否已入库
                     if (_novelInfoRepository.Exists(x => x.Name == info.Name && x.Author == info.Author))
                     {
                         await ProcessUpdate(spider, novelKey, info);//更新
                     }
                     else
                     {
                         await ProcessAdd(spider, novelKey, info);//新增
                     }

                 }
                 catch (SpiderException ex)
                 {
                     Logger.Error("{0}，{1} 小说详情抓取失败：{2}", rule.SiteUrl, novelKey, ex.Message);
                 }
                 catch (Exception ex)
                 {
                     Logger.Fatal(ex, "ProcessEngine.Process");
                 }
             });
            Logger.ColorConsole("本次抓取结束");
        }


        private async Task ProcessAdd(Spider spider, string novelKey, NovelDetails info)
        {
            var chapterIndex = info.ChapterIndex;
            //小说id
            var novelId = ObjectId.NextId();
            //目录索引id
            var novelIndexId = ObjectId.NextId();
            //小说封面
            var novelCover = spider.DownLoadImageToBase64(info.ImageUrl);

            /*
             * 1 >>> 获取章节列表
             */
            var chapterList = await spider.GetNovelChapterList(novelKey, chapterIndex);
            var indexes = new List<Index>();
            //抓取章节  单个抓取 需要延迟 不然容易被封
            for (int i = 0; i < chapterList.Count; i++)
            {
                var chapter = chapterList[i];
                try
                {
                    var content = await spider.GetContent(novelKey, chapterIndex, chapter.Value);
                    var chapterId = ObjectId.NextId();
                    var chapterEntity = new NovelChapter()
                    {
                        Id = chapterId,
                        NovelId = novelId,
                        ChapterName = chapter.Key,
                        UpdateTime = DateTime.Now,
                        WordCount = Utils.GetWordCount(content),
                        Content = content
                    };
                    _novelChapterRepository.Insert(novelId, chapterEntity);
                    indexes.Add(new Index() { ChapterId = chapterId, ChapterName = chapter.Key });//索引目录
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    Logger.Error("{0}-{1} 小说章节抓取失败：{2}", chapter.Key, chapter.Value, ex.Message);
                    //单章节 抓取失败
                    //todo 策略？ 1.终止  2.跳过失败章节 
                }
            }

            /*
             * 2 >>> 写入索引目录
             */
            var novelIndex = new NovelIndex()
            {
                Id = novelIndexId,
                NovelId = novelId,
                UpdateTime = DateTime.Now,
                Indexex = indexes
            };
            _novelIndexRepository.Insert(novelIndex);

            /*
             * 3 >>> 写入小说详情
             */
            var novelInfo = new NovelInfo()
            {
                Id = novelId,
                Name = info.Name,
                Author = info.Author,
                Sort = info.Sort,
                State = info.State,
                Des = info.Des,
                Cover = novelCover,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now,
                LatestChapter = indexes.LastOrDefault()?.ChapterName,
                LatestChapterId = indexes.LastOrDefault()?.ChapterId,
                IndexId = novelIndexId
            };
            _novelInfoRepository.Insert(novelInfo);
        }


        private async Task ProcessUpdate(Spider spider, string novelKey, NovelDetails info)
        {
            var chapterIndex = info.ChapterIndex;
            var novelInfo = _novelInfoRepository.FindOrDefault(x => x.Name == info.Name && x.Author == info.Author);
            if (novelInfo == null)
                return;
            //对比章节，判断是否需要新增
            var oldIndexes = _novelIndexRepository.FindOrDefault(x => x.Id == novelInfo.IndexId);//老索引
            var oldChapterList = oldIndexes?.Indexex.Select(x => x.ChapterName).ToList();//老的章节列表
            var chapterList = await spider.GetNovelChapterList(novelKey, chapterIndex);//抓取最新章节
            var newChapterList = chapterList.Select(x => x.Key).ToList();//新的章节列表
            int updateIndex = 0;

            if (ChapterListNeedUpdate(oldChapterList, newChapterList, out updateIndex))
            {
                var indexes = new List<Index>();//更新的列表
                //更新章节
                for (int i = updateIndex; i < chapterList.Count; i++)
                {
                    var chapter = chapterList[i];
                    try
                    {
                        var content = await spider.GetContent(novelKey, chapterIndex, chapter.Value);
                        var chapterId = ObjectId.NextId();
                        var chapterEntity = new NovelChapter()
                        {
                            Id = chapterId,
                            NovelId = novelInfo.Id,
                            ChapterName = chapter.Key,
                            UpdateTime = DateTime.Now,
                            WordCount = Utils.GetWordCount(content),
                            Content = content
                        };
                        _novelChapterRepository.Insert(novelInfo.Id, chapterEntity);
                        indexes.Add(new Index() { ChapterId = chapterId, ChapterName = chapter.Key });//索引目录
                        Thread.Sleep(500);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("{0}-{1} 小说章节抓取失败：{2}", chapter.Key, chapter.Value, ex.Message);
                        //单章节 抓取失败
                        //todo 策略？ 1.终止  2.跳过失败章节 
                    }
                }
                //更新索引目录
                oldIndexes.Indexex.AddRange(indexes);
                _novelIndexRepository.Update(x => x.Id == oldIndexes.Id, oldIndexes);
                //更新小说详情
                novelInfo.State = info.State;
                novelInfo.UpdateTime = DateTime.Now;
                novelInfo.LatestChapter = oldIndexes.Indexex.LastOrDefault()?.ChapterName;
                novelInfo.LatestChapterId = oldIndexes.Indexex.LastOrDefault()?.ChapterId;
                _novelInfoRepository.Update(x => x.Id == novelInfo.Id, novelInfo);
            }


        }

        /// <summary>
        /// 章节列表是否需要更新
        /// </summary>
        private bool ChapterListNeedUpdate(List<string> oldIndexes, List<string> newIndexes, out int num)
        {
            double similarity;
            num = 0;
            if (oldIndexes == null || oldIndexes.Count == 0)
                return true;
            //对比 最后一章
            for (int i = newIndexes.Count - 1; i >= 0; i++)
            {
                var oldChapter = oldIndexes.LastOrDefault();
                var newChapter = newIndexes[i];
                if (oldChapter == newChapter || Utils.CompareChapter(oldChapter, newChapter, out similarity))
                {
                    num = i + 1;
                    return !(i == newIndexes.Count - 1);//如果是最后一章 不更新
                }
            }

            //对比 倒数第二章
            if (oldIndexes.Count >= 2)
            {
                for (int i = newIndexes.Count - 1; i >= 0; i++)
                {
                    var oldChapter = oldIndexes[oldIndexes.Count - 2];
                    var newChapter = newIndexes[i];
                    if (oldChapter == newChapter || Utils.CompareChapter(oldChapter, newChapter, out similarity))
                    {
                        num = i + 1;
                        return !(i == newIndexes.Count - 1);//如果是最后一章 不更新
                    }
                }
            }

            //如果库里有数据，是一定不会到这里！除非一章都没对上
            Logger.Error("章节列表对比失败！");
            return false;
        }


    }
}
