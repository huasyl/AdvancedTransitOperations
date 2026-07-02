using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal static class Diagnostics
    {
        internal static bool Enabled => RtLog.VerboseEnabled;

        internal static void Log(string tag, string message)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(message))
                return;
            Mod.log.Info("[" + tag + "] " + message);
        }

        internal static string DescribeEntity(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString();
        }

        internal static string DescribeMode(TransitMode mode)
        {
            return TransitModeCodec.Format(mode);
        }

        internal static string DescribeBucket(TimeBucketKey bucket)
        {
            return bucket.ServiceDayIndex.ToString() + ":" + bucket.BucketStartMinute.ToString();
        }

        internal static string DescribeCount<T>(T[] rows)
        {
            return (rows != null ? rows.Length : 0).ToString();
        }

    }
}
