﻿using System;
using System.Collections.Generic;
using System.Text;

namespace NovelCrawler.Processer.Models
{
    public class NovelInfo
    {
        public string Name { get; set; }
        public string ImageUrl { get; set; }
        public string Classify { get; set; }
        public string Author { get; set; }
        public string State { get; set; }
        public string Des { get; set; }

        public string ChapterIndex { get; set; }
    }
}