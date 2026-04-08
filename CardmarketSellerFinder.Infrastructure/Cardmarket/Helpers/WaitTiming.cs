using System;
using System.Collections.Generic;
using System.Text;

namespace CMBuyerStudio.Infrastructure.Cardmarket.Helpers
{
    public static class WaitTiming
    {
        public static int GetRandom(int min, int max)
        {
            return Random.Shared.Next(min, max);
        }

        public static int GetRandom(int delay)
        {
            return Random.Shared.Next(delay, delay*2);
        }

        public static int GetRandomTypingDelay()
        {
            return GetRandom(80, 100);
        }
    }
}
