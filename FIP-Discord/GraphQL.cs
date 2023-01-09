namespace FIP
{
    public record GraphQL
    {
        public required string query { init; get; }
    }

    public record GraphQLResult
    {
        public Data data { set; get; }
    }

    public record Data
    {
        public Live live { set; get; }
    }

    public record Live
    {
        public Song song { set; get; }
    }

    public record Song
    {
        public int end { set; get; }
        public Track track { set; get; }
    }

    public record Track
    {
        public string title { set; get; }
        public string albumTitle { set; get; }
        public string[] mainArtists { set; get; }
    }
}
