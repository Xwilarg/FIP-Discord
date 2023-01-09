using Discord;
using Discord.Audio;
using Discord.WebSocket;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Web;

namespace FIP
{
    public sealed class Program
    {
        private readonly DiscordSocketClient _client = new(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates
        });

        public static async Task Main()
        {
            await new Program().StartAsync();
        }

        private string _openRadioApiToken;
        private string _lastFmApiToken;
        private HttpClient _http = new();

        public async Task StartAsync()
        {
            await Log.LogAsync(new LogMessage(LogSeverity.Info, "Setup", "Initialising bot"));

            // Setting Logs callback
            _client.Log += Log.LogAsync;

            // Load credentials
            if (!File.Exists("Keys/Credentials.json"))
                throw new FileNotFoundException("Missing Credentials file");
            var credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText("Keys/Credentials.json"));
            if (credentials == null || credentials.BotToken == null || credentials.OpenRadioApiToken == null || credentials.LastFmApiToken == null)
                throw new NullReferenceException("Missing credentials");
            _openRadioApiToken = credentials.OpenRadioApiToken;
            _lastFmApiToken = credentials.LastFmApiToken;

            _client.Ready += Ready;
            _client.SlashCommandExecuted += SlashCommandExecuted;

            await _client.LoginAsync(TokenType.Bot, credentials.BotToken);
            await _client.StartAsync();

            new Thread(new ThreadStart(WatchOver)).Start();

            // We keep the bot online
            await Task.Delay(-1);
        }

        private readonly Dictionary<ulong, IMessageChannel> _followChans = new();
        private readonly Dictionary<ulong, SocketVoiceChannel> _audioChannels = new();
        private Song? _currentSong;
        private void WatchOver()
        {
            while (Thread.CurrentThread.IsAlive)
            {
                while (_followChans.Any())
                {
                    var previous = _currentSong?.end;
                    UpdateCurrentSongAsync().GetAwaiter().GetResult();
                    if (_currentSong != null && previous != _currentSong.end)
                    {
                        var embed = GetSongEmbedAsync().GetAwaiter().GetResult();
                        foreach (var chan in _followChans)
                        {
                            try
                            {
                                chan.Value.SendMessageAsync(embed: embed).GetAwaiter().GetResult();
                            }
                            catch { }
                        }
                    }
                }
                Thread.Sleep(200);
            }
        }

        private async Task UpdateCurrentSongAsync()
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            int epochTime = (int)t.TotalSeconds;
            if (_currentSong != null && _currentSong.end > epochTime)
            {
                return;
            }
            var json = JsonSerializer.Serialize(new GraphQL
            {
                query = "{ live(station: FIP) { song { end, track {title albumTitle mainArtists} } } }"
            });

            var answer = await _http.PostAsync($"https://openapi.radiofrance.fr/v1/graphql?x-token={_openRadioApiToken}", new StringContent(json, Encoding.UTF8, "application/json"));
            answer.EnsureSuccessStatusCode();

            var text = await answer.Content.ReadAsStringAsync();
            _currentSong = JsonSerializer.Deserialize<GraphQLResult>(text).data.live.song;
        }

        private async Task<Embed> GetSongEmbedAsync()
        {
            var lastFm = JsonSerializer.Deserialize<LastFm>(await _http.GetStringAsync($"https://ws.audioscrobbler.com/2.0/?method=track.getInfo&api_key={HttpUtility.UrlEncode(_lastFmApiToken)}&artist={_currentSong.track.mainArtists.FirstOrDefault()}&track={HttpUtility.UrlEncode(_currentSong.track.title)}&format=json"));
            return new EmbedBuilder
            {
                Title = $"{_currentSong.track.title} by {string.Join(", ", _currentSong.track.mainArtists)}",
                Fields = new()
                {
                    new()
                    {
                        Name = "Album",
                        Value = _currentSong.track.albumTitle,
                        IsInline = true
                    },
                    new()
                    {
                        Name = "Ends",
                        Value = $"<t:{_currentSong.end}:R>",
                        IsInline = true
                    }
                },
                ImageUrl = lastFm?.track?.album?.image?.LastOrDefault()?.text,
                Url = lastFm?.track?.url,
                Color = new(227, 0, 123)
            }.Build();
        }

        private async Task SlashCommandExecuted(SocketSlashCommand arg)
        {
            if (arg.CommandName == "play")
            {
                if (arg.User is not IGuildUser guildUser)
                {
                    await arg.RespondAsync("This command can only be done in a guild", ephemeral: true);
                    return;
                }
                if (guildUser.VoiceChannel == null)
                {
                    await arg.RespondAsync("You must be in a voice channel to do this command", ephemeral: true);
                    return;
                }
                await arg.RespondAsync("Starting the radio...");
                _ = Task.Run(async () =>
                {
                    var vChan = (SocketVoiceChannel)guildUser.VoiceChannel;
                    var timer = new System.Timers.Timer()
                    {
                        Interval = 10000
                    };
                    timer.Elapsed += async (sender, _) =>
                    {
                        if (vChan.ConnectedUsers.Count == 1)
                        {
                            await arg.Channel.SendMessageAsync("No user left in the channel, ending radio...");
                            await vChan.DisconnectAsync();
                            timer.Enabled = false;
                        }
                    };
                    timer.Start();
                    await UpdateCurrentSongAsync();
                    await arg.Channel.SendMessageAsync(embed: await GetSongEmbedAsync());
                    _followChans.Add(arg.Channel.Id, arg.Channel);
                    _audioChannels.Add(arg.GuildId!.Value, vChan);
                    try
                    {
                        var audioClient = await guildUser.VoiceChannel.ConnectAsync();
                        var ffmpeg = Process.Start(new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-hide_banner -loglevel panic -i https://icecast.radiofrance.fr/fip-midfi.mp3 -ac 2 -f s16le -ar 48000 pipe:",
                            UseShellExecute = false,
                            RedirectStandardOutput = true
                        });
                        using var output = ffmpeg.StandardOutput.BaseStream;
                        using var discord = audioClient.CreatePCMStream(AudioApplication.Mixed);
                        try { await output.CopyToAsync(discord); }
                        finally
                        {
                            await discord.FlushAsync();
                            _followChans.Remove(arg.Channel.Id);
                            _audioChannels.Remove(arg.GuildId.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        await arg.Channel.SendMessageAsync(ex.Message);
                        _followChans.Remove(arg.Channel.Id);
                        _audioChannels.Remove(arg.GuildId.Value);
                    }
                });
            }
            else if (arg.CommandName == "stop")
            {
                if (arg.GuildId == null)
                {
                    await arg.RespondAsync("This command can only be done in a guild", ephemeral: true);
                }
                else if (!_audioChannels.ContainsKey(arg.GuildId.Value))
                {
                    await arg.RespondAsync("No radio was start in this server", ephemeral: true);
                }
                else
                {
                    await arg.RespondAsync("Stopping the radio...");
                    await _audioChannels[arg.GuildId.Value].DisconnectAsync();
                }
            }
            else if (arg.CommandName == "github")
            {
                await arg.RespondAsync("https://github.com/Xwilarg/FIP-Discord/", ephemeral: true);
            }
            else if (arg.CommandName == "invite")
            {
                await arg.RespondAsync("https://discord.com/api/oauth2/authorize?client_id=1062043607252606976&permissions=3145728&scope=bot%20applications.commands", ephemeral: true);
            }
        }

        private bool _isInit = false;
        private async Task Ready()
        {
            if (!_isInit)
            {
                _isInit = true;
                _ = Task.Run(async () =>
                {
                    var commands = new SlashCommandBuilder[]
                    {
                        new()
                        {
                            Name = "play",
                            Description = "Start playing the radio in the vocal channel where the user is"
                        },
                        new()
                        {
                            Name = "stop",
                            Description = "Stop playing the radio"
                        },
                        new()
                        {
                            Name = "github",
                            Description = "Get the link to the source code of the bot"
                        },
                        new()
                        {
                            Name = "invite",
                            Description = "Get the invite link of the bot"
                        }
                    }.Select(x => x.Build()).ToArray();
                    foreach (var cmd in commands)
                    {
                        if (Debugger.IsAttached)
                        {
                            // That's where I debug my stuffs, yeah I'm sorry about hardcoding stuffs
                            await _client.GetGuild(832001341865197579).CreateApplicationCommandAsync(cmd);
                        }
                        else
                        {
                            await _client.CreateGlobalApplicationCommandAsync(cmd);
                        }
                    }
                    if (Debugger.IsAttached)
                    {
                        await _client.GetGuild(832001341865197579).BulkOverwriteApplicationCommandAsync(commands);
                    }
                    else
                    {
                        await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
                    }
                });
            }
        }
    }
}