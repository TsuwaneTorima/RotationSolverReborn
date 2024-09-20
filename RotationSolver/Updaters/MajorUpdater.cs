﻿using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.GeneratedSheets;
using RotationSolver.Commands;
using RotationSolver.Data;

using RotationSolver.UI.HighlightTeachingMode;
using System.Runtime.InteropServices;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;

namespace RotationSolver.Updaters;

internal static class MajorUpdater
{
    public static bool IsValid => Svc.Condition.Any()
        && !Svc.Condition[ConditionFlag.BetweenAreas]
        && !Svc.Condition[ConditionFlag.BetweenAreas51]
        && !Svc.Condition[ConditionFlag.LoggingOut]
        && Player.Available;

    private static bool _work;
    private static Exception? _threadException;
    private static DateTime _lastUpdatedWork = DateTime.Now;
    private static DateTime _warningsLastDisplayed = DateTime.MinValue;

    private unsafe static void FrameworkUpdate(IFramework framework)
    {
        HotbarHighlightManager.HotbarIDs.Clear();
        RotationSolverPlugin.UpdateDisplayWindow();

        if (!IsValid)
        {
            ActionUpdater.ClearNextAction();
            CustomRotation.MoveTarget = null;
            return;
        }

        HandleSystemWarnings();

        try
        {
            PreviewUpdater.UpdatePreview();
            UpdateHighlight();
            ActionUpdater.UpdateActionInfo();

            var canDoAction = ActionUpdater.CanDoAction();
            MovingUpdater.UpdateCanMove(canDoAction);

            if (canDoAction)
            {
                RSCommands.DoAction();
            }

            MacroUpdater.UpdateMacro();
            CloseWindow();
            OpenChest();
        }
        catch (Exception ex)
        {
            if (_threadException != ex)
            {
                _threadException = ex;
                Svc.Log.Error(ex, "Main Thread Exception");
                if (Service.Config.InDebug)
#pragma warning disable CS0436
                    WarningHelper.AddSystemWarning("Main Thread Exception");
            }
        }

        HandleWorkUpdate();
    }

    private static void HandleSystemWarnings()
    {
        if (DataCenter.SystemWarnings.Any())
        {
            var warningsToRemove = new List<string>();

            foreach (var warning in DataCenter.SystemWarnings)
            {
                if ((warning.Value + TimeSpan.FromMinutes(10)) < DateTime.Now)
                {
                    warningsToRemove.Add(warning.Key);
                }
            }

            foreach (var warningKey in warningsToRemove)
            {
                DataCenter.SystemWarnings.Remove(warningKey);
            }
        }
    }

    private static readonly object _workLock = new object();

    private static void HandleWorkUpdate()
    {
        var now = DateTime.UtcNow;
        try
        {
            lock (_workLock)
            {
                if (_work || (now - _lastUpdatedWork < TimeSpan.FromSeconds(Service.Config.MinUpdatingTime)))
                    return;

                _work = true;
                _lastUpdatedWork = now;
            }

            Task.Run(UpdateWork).ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    Svc.Log.Error(t.Exception, "Worker Task Exception");
                    if (Service.Config.InDebug)
#pragma warning disable CS0436
                        WarningHelper.AddSystemWarning("Worker Task Exception");
                }
                _work = false;
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Worker Exception in HandleWorkUpdate");
            _work = false;
            if (Service.Config.InDebug)
#pragma warning disable CS0436
                WarningHelper.AddSystemWarning("Worker Exception in HandleWorkUpdate");
        }
    }

    private static void UpdateWork()
    {
        var now = DateTime.UtcNow;
        var waitingTime = (now - _lastUpdatedWork).TotalMilliseconds;
        if (waitingTime > 100)
        {
            Svc.Log.Warning($"The time for completing a running cycle for RS is {waitingTime:F2} ms, try disabling the option \"UseWorkTask\" to get better performance or check your other running plugins for one of them using too many resources and try disabling that.");
        }

        if (!IsValid)
        {
            ActionUpdater.NextAction = ActionUpdater.NextGCDAction = null;
            return;
        }

        try
        {
            StateUpdater.UpdateState();

            if (Service.Config.AutoReloadRotations)
            {
                RotationUpdater.LocalRotationWatcher();
            }

            RotationUpdater.UpdateRotation();

            if (DataCenter.IsActivated())
            {
                TargetUpdater.UpdateTarget();
                ActionSequencerUpdater.UpdateActionSequencerAction();
                ActionUpdater.UpdateNextAction();
            }

            RSCommands.UpdateRotationState();
            HotbarHighlightManager.UpdateSettings();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Inner Worker Exception");
            if (Service.Config.InDebug)
#pragma warning disable CS0436
                WarningHelper.AddSystemWarning("Inner Worker Exception");
        }
        finally
        {
            _work = false;
        }
    }

    private static void UpdateHighlight()
    {
        if (!Service.Config.TeachingMode || ActionUpdater.NextAction is not IAction nextAction) return;

        HotbarID? hotbar = nextAction switch
        {
            IBaseItem item => new HotbarID(HotbarSlotType.Item, item.ID),
            IBaseAction baseAction when baseAction.Action.ActionCategory.Row is 10 or 11 => Svc.Data.GetExcelSheet<GeneralAction>()?.FirstOrDefault(g => g.Action.Row == baseAction.ID) is GeneralAction gAct ? new HotbarID(HotbarSlotType.GeneralAction, gAct.RowId) : null,
            IBaseAction baseAction => new HotbarID(HotbarSlotType.Action, baseAction.AdjustedID),
            _ => null
        };

        if (hotbar.HasValue)
        {
            HotbarHighlightManager.HotbarIDs.Add(hotbar.Value);
        }
    }

    private static void ShowWarning()
    {
        if (!Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Avarice"))
        {
#pragma warning disable CS0436
            WarningHelper.AddSystemWarning(UiString.AvariceWarning.GetDescription());
        }
        if (!Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "TextToTalk"))
        {
#pragma warning disable CS0436
            WarningHelper.AddSystemWarning(UiString.TextToTalkWarning.GetDescription());
        }
    }

    public static void Enable()
    {
        ActionSequencerUpdater.Enable(Svc.PluginInterface.ConfigDirectory.FullName + "\\Conditions");
        Svc.Framework.Update += FrameworkUpdate;
    }

    static DateTime _closeWindowTime = DateTime.Now;
    private unsafe static void CloseWindow()
    {
        if (_closeWindowTime < DateTime.Now) return;

        var needGreedWindow = Svc.GameGui.GetAddonByName("NeedGreed", 1);
        if (needGreedWindow == IntPtr.Zero) return;

        var notification = (AtkUnitBase*)Svc.GameGui.GetAddonByName("_Notification", 1);
        if (notification == null) return;

        var atkValues = (AtkValue*)Marshal.AllocHGlobal(2 * sizeof(AtkValue));
        atkValues[0].Type = atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        atkValues[0].Int = 0;
        atkValues[1].Int = 2;
        try
        {
            notification->FireCallback(2, atkValues);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Failed to close the window!");
        }
        finally
        {
            Marshal.FreeHGlobal(new IntPtr(atkValues));
        }
    }

    static DateTime _nextOpenTime = DateTime.Now;
    static ulong _lastChest = 0;
    private unsafe static void OpenChest()
    {
        if (!Service.Config.AutoOpenChest) return;
        var player = Player.Object;

        var treasure = Svc.Objects.FirstOrDefault(o =>
        {
            if (o == null) return false;
            var dis = Vector3.Distance(player.Position, o.Position) - player.HitboxRadius - o.HitboxRadius;
            if (dis > 0.5f) return false;

            var address = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)(void*)o.Address;
            if ((ObjectKind)address->ObjectKind != ObjectKind.Treasure) return false;

            //Opened!
            foreach (var item in Loot.Instance()->Items)
            {
                if (item.ChestObjectId == o.GameObjectId) return false;
            }

            return true;
        });

        if (treasure == null) return;
        if (DateTime.Now < _nextOpenTime) return;
        if (treasure.GameObjectId == _lastChest && DateTime.Now - _nextOpenTime < TimeSpan.FromSeconds(10)) return;

        _nextOpenTime = DateTime.Now.AddSeconds(new Random().NextDouble() + 0.2);
        _lastChest = treasure.GameObjectId;

        try
        {
            Svc.Targets.Target = treasure;

            TargetSystem.Instance()->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)(void*)treasure.Address);

            Notify.Plain($"Try to open the chest {treasure.Name}");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to open the chest!");
        }

        if (!Service.Config.AutoCloseChestWindow) return;
        _closeWindowTime = DateTime.Now.AddSeconds(0.5);
    }

    public static void Dispose()
    {
        Svc.Framework.Update -= FrameworkUpdate;
        PreviewUpdater.Dispose();
        ActionSequencerUpdater.SaveFiles();
        ActionUpdater.ClearNextAction();
    }
}
