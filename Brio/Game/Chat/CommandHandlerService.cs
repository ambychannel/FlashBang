using Brio.Services;
using Brio.UI;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using System;

namespace Brio.Game.Chat;

public class CommandHandlerService : IDisposable
{
    private const string BrioCommandName = "/flashbang";
    private const string XATCommandName = "/fbxat";
    private const string MCDFCommandName = "/fbmcdf";

    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chatGui;
    private readonly UIManager _uiManager;
    private readonly Mediator _mediator;

    public CommandHandlerService(ICommandManager commandManager, IChatGui chatGui, UIManager uiManager, Mediator mediator)
    {
        _commandManager = commandManager;
        _chatGui = chatGui;
        _uiManager = uiManager;
        _mediator = mediator;

        _commandManager.AddHandler(BrioCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles the FlashBang window.",
            ShowInHelp = true,
        });
        _commandManager.AddHandler(XATCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles the FlashBang window.",
            ShowInHelp = false,
        });
        _commandManager.AddHandler(MCDFCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles FlashBang's MCDF window.",
            ShowInHelp = false,
        });
    }

    private void OnCommand(string command, string arguments)
    {
        if(command == MCDFCommandName)
        {
            _uiManager.ToggleMCDFWindow();
            return;
        }

        if(arguments.Length == 0)
            arguments = "window";

        var argumentList = arguments.Split(' ', 2);

        switch(argumentList[0].ToLowerInvariant())
        {
            case "window":
                _uiManager.ToggleMainWindow();
                break;

            case "timeline":
                _uiManager.ToggleTimelineWindow();
                break;

            case "settings":
                _uiManager.ToggleSettingsWindow();
                break;

            case "about":
                _uiManager.ToggleWelcomeWindow();
                break;

            case "mcdf":
                _uiManager.ToggleMCDFWindow();
                break;

            case "sync":
                _uiManager.ToggleSyncWindow();
                break;

            case "mediator":
                _mediator.PrintSubscriberInfo();
                break;

            case "help":
            default:
                PrintHelp();
                break;
        }

    }

    private void PrintHelp()
    {
        _chatGui.Print("Valid FlashBang Commands Are:");
        _chatGui.Print("<none> - Toggle main FlashBang window");
        _chatGui.Print("window - Toggle main FlashBang window");
        _chatGui.Print("sync - Toggle the Sync window (rooms and agreeing to sync)");
        _chatGui.Print("settings - Toggle FlashBang settings window");
        _chatGui.Print("about - Toggle FlashBang info window");
        _chatGui.Print("help - Print this help prompt");
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(BrioCommandName);
        _commandManager.RemoveHandler(XATCommandName);
        _commandManager.RemoveHandler(MCDFCommandName);
    }
}
