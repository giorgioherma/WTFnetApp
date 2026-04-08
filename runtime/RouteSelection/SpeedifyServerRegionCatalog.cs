using System.Collections.Generic;

namespace WTFnetApp
{
    public sealed class SpeedifyServerRegion
    {
        public string CountryCode { get; set; }
        public string FriendlyName { get; set; }
        public string ConnectTarget { get; set; }

        public override string ToString()
        {
            return FriendlyName ?? CountryCode ?? "unknown";
        }
    }

    public static class SpeedifyServerRegionCatalog
    {
        private static readonly IReadOnlyList<SpeedifyServerRegion> defaultRegions =
            new List<SpeedifyServerRegion>
            {
                new SpeedifyServerRegion
                {
                    CountryCode = "si",
                    FriendlyName = "Slovenia",
                    ConnectTarget = "si"
                },
                new SpeedifyServerRegion
                {
                    CountryCode = "at",
                    FriendlyName = "Austria",
                    ConnectTarget = "at"
                },
                new SpeedifyServerRegion
                {
                    CountryCode = "it",
                    FriendlyName = "Italy",
                    ConnectTarget = "it"
                },
                new SpeedifyServerRegion
                {
                    CountryCode = "hu",
                    FriendlyName = "Hungary",
                    ConnectTarget = "hu"
                },
                new SpeedifyServerRegion
                {
                    CountryCode = "de",
                    FriendlyName = "Germany",
                    ConnectTarget = "de"
                }
            };

        public static IReadOnlyList<SpeedifyServerRegion> GetDefaultRegions()
        {
            return defaultRegions;
        }
    }
}