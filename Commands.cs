using Discord.WebSocket;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Linq;
using System.Data;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace neogary 
{
    public class Commands
    {
        private string _prefix;
        private DiscordSocketClient _client;
        private CommandService _commands;
        private IServiceProvider _services;
        private ILogService _log;
        private IDataService _data;
        private PermissionService _perms;

        private readonly int _defaultPermTier = -1;

        struct Command
        {
            public string Name;
            public string Description;
            public string Module;
        }

        public Commands(string commandPrefix, DiscordSocketClient client, IServiceProvider services)
        {
            _prefix = commandPrefix;
            _services = services;
            _log = services.GetService<ILogService>();
            _data = services.GetService<IDataService>();
            _perms = services.GetService<PermissionService>();
            
            _client = client;
            _client.MessageReceived += HandleCommand;

            _commands = new CommandService();
            _commands.AddModulesAsync(Assembly.GetEntryAssembly());

            UpdateCommandsInDb();
        }

        private void UpdateCommandsInDb()
        {
            var dbCommands = new List<Command>(); 
            _data.Find(
                "botcommand", 
                "1=1", 
                r => dbCommands
                    .Add(new Command
                    { 
                        Name = r.GetString(1),
                        Description = r.GetString(2),
                        Module = r.GetString(4)
                    }));

            var moduleCommands = _commands.Commands
                .Select(c => new Command
                {
                    Name = c.Name,
                    Description = c.Remarks,
                    Module = c.Module.Name
                })
                .ToList();

            var allCommands = dbCommands.Concat(moduleCommands);

            // keep track of the updated commands so as not to update them twice
            List<string> updatedCommands = new List<string>();
            foreach(var c in allCommands)
            {
                if (updatedCommands.Contains(c.Name))
                    continue;

                bool inModules = moduleCommands.Any(cmd => cmd.Name == c.Name);
                bool inDb = dbCommands.Any(cmd => cmd.Name == c.Name);

                if (inModules && inDb)
                {
                    // compare descriptions, update database with module version
                    var dbCmd = dbCommands.Single(cmd => cmd.Name == c.Name);
                    var mdCmd = moduleCommands.Single(cmd => cmd.Name == c.Name);

                    if (dbCmd.Description != mdCmd.Description)
                    {
                        _data.Update(
                            "botcommand",
                            String.Format("description='{0}'", mdCmd.Description),
                            String.Format("name='{0}'", c.Name));
                        updatedCommands.Add(c.Name);
                    }
                }

                if (inDb && !inModules)
                {
                    _data.Remove(
                        "botcommand",
                        String.Format("name = '{0}'", c.Name));
                    updatedCommands.Add(c.Name);
                }
                else if (!inDb && inModules)
                {
                    _data.Insert(
                        "botcommand",
                        "name, description, permtier, module",
                        String.Format(
                            "'{0}','{1}', {2}, '{3}'", 
                            c.Name, 
                            c.Description, 
                            _defaultPermTier,
                            c.Module));
                    updatedCommands.Add(c.Name);
                }
            }

            _log.Log(
                String.Format(
                    "updated {0} commands in DB", 
                    updatedCommands.Count));
        }

        private async Task HandleCommand(SocketMessage socketMessage)
        {
            var message = (SocketUserMessage)socketMessage;

            // Ignore messages sent by the bot itself
            if (message.Author.Id == _client.CurrentUser.Id)
                return;

            if (!message.Content.StartsWith(_prefix))
                return;

            _log.Log(
                String.Format(
                    "{0}:\t{1}", 
                    message.Author.Username, 
                    message.Content));
            
            if (!CheckPermission(message))
                return;

            // where the actual command starts, after the prefix
            int argPos = _prefix.Length;
            var result = await _commands.ExecuteAsync(
                new CommandContext(_client, message), 
                argPos, 
                _services);
            if (!result.IsSuccess)
            {
                _log.Log(
                    String.Format(
                        "command failed:\t{0}", 
                        result.ErrorReason));
            } 
        }

        private bool CheckPermission(SocketUserMessage message)
        {
            var author = (SocketGuildUser)message.Author;
            var commandName = message
                .Content
                .Split(' ')
                .First()
                .Substring(_prefix.Length);

            return _perms.CheckPermission(author, commandName);
        }
    }
}
