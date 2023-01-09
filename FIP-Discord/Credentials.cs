namespace FIP
{
    public record Credentials
    {
        public string BotToken { init; get; }
        public string OpenRadioApiToken { init; get; }
        public string LastFmApiToken { init; get; }
    }
}