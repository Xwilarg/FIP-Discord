using System.Text.Json.Serialization;

namespace FIP
{
    public record LastFm
    {
        public LastFmTrack track { set; get; }
    }

    public record LastFmTrack
    {
        public Album album { set; get; }
        public string url { set; get; }
    }

    public record Album
    {
        public Image[] image { set; get; }
    }

    public record Image
    {
        [JsonPropertyName("#text")]
        public string text { set; get; }
    }
}
