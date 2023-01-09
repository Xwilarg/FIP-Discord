using Discord;
using Discord.Audio;
using Discord.WebSocket;
using System.Diagnostics;
using System.Text.Json;

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

        public async Task StartAsync()
        {
            await Log.LogAsync(new LogMessage(LogSeverity.Info, "Setup", "Initialising bot"));

            // Setting Logs callback
            _client.Log += Log.LogAsync;

            // Load credentials
            if (!File.Exists("Keys/Credentials.json"))
                throw new FileNotFoundException("Missing Credentials file");
            var credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText("Keys/Credentials.json"));
            if (credentials == null)
                throw new NullReferenceException("No token found");

            _client.Ready += Ready;
            _client.SlashCommandExecuted += SlashCommandExecuted;

            await _client.LoginAsync(TokenType.Bot, credentials.BotToken);
            await _client.StartAsync();

            // We keep the bot online
            await Task.Delay(-1);
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
                        finally { await discord.FlushAsync(); }
                    }
                    catch (Exception ex)
                    {
                        await arg.Channel.SendMessageAsync(ex.Message);
                    }
                });
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