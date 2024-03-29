﻿namespace FIP_Discord
{
    public enum FIPChannel
    {
        FIP,
        POP,
        JAZZ,
        ROCK,
        METAL,
        WORLD,
        GROOVE,
        REGGAE,
        ELECTRO,
        HIP_HOP,
        NOUVEAUTES
    }

    public static class FIPChannelInfo
    {
        public static string GetStreamFlux(FIPChannel fip)
        {
            return fip switch
            {
                FIPChannel.FIP => "https://icecast.radiofrance.fr/fip-midfi.mp3",
                _ => $"https://icecast.radiofrance.fr/fip{fip.ToString().ToLowerInvariant().Replace("_", "")}-midfi.mp3"
            };
        }

        public static string GetStationName(FIPChannel fip)
        {
            return fip switch
            {
                FIPChannel.FIP => "FIP",
                _ => $"FIP_{fip}"
            };
        }
    }
}
