using System;

namespace WTFnetApp
{
    public sealed class SpeedifyActivatedServerIdentity
    {
        public SpeedifyActivatedServerIdentity(
            string requestedTarget,
            DateTime capturedUtc,
            string friendlyName,
            string tag,
            string publicIp,
            string country,
            string city,
            string dataCenter,
            bool isPremium,
            int num)
        {
            RequestedTarget = requestedTarget;
            CapturedUtc = capturedUtc;
            FriendlyName = friendlyName;
            Tag = tag;
            PublicIp = publicIp;
            Country = country;
            City = city;
            DataCenter = dataCenter;
            IsPremium = isPremium;
            Num = num;
        }

        public string RequestedTarget { get; }
        public DateTime CapturedUtc { get; }
        public string FriendlyName { get; }
        public string Tag { get; }
        public string PublicIp { get; }
        public string Country { get; }
        public string City { get; }
        public string DataCenter { get; }
        public bool IsPremium { get; }
        public int Num { get; }
    }
}
