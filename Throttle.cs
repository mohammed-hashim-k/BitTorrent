using System;
using System.Linq;
using System.Collections.Generic;

namespace BitTorrent
{
    public class Throttle
    {
        public long MaximumSize { get; private set; }
        public TimeSpan MaximumWindow { get; private set; }

        // represents an item in the throttle, with a timestamp and size = the amount of data downloaded or uploaded at that time
        internal struct Item
        {
            public DateTime Time;
            public long Size;
        }

        private object itemLock = new object();
        private List<Item> items = new List<Item>();

        public Throttle(int maxSize, TimeSpan maxWindow)
        {
            MaximumSize = maxSize;
            MaximumWindow = maxWindow;
        }

        public void Add(long size)
        {
            lock (itemLock)
            {
                items.Add(new Item() { Time = DateTime.UtcNow, Size = size });
            }
        }

        public bool IsThrottled
        {
            get
            {
                lock (itemLock)
                {
                    DateTime cutoff = DateTime.UtcNow.Add(-this.MaximumWindow);
                    items.RemoveAll(x => x.Time < cutoff);
                    return items.Sum(x => x.Size) >= MaximumSize;
                }
            }
        }
    }
}