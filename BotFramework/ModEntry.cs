using BotFramework.Helpers;
using BotFramework.Navigation;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Microsoft.Xna.Framework;

namespace BotFramework
{
    /// <summary>
    /// The mod entry point.
    /// </summary>
    public class ModEntry : Mod
    {
        /// <summary>
        /// Mod configuration
        /// </summary>
        public ModConfig config;

        private PlayerNavigator _navigator;

        /// <summary>
        /// The mod entry point, called after the mod is first loaded.
        /// </summary>
        /// 
        /// <param name="helper">Provides simplified APIs for writing mods</param>
        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.GameLaunched += this.onLaunched;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;

            helper.ConsoleCommands.Add("bf_go", "BotFramework: 导航到指定地点（模糊匹配）。用法：bf_go <locationName>", this.CmdGo);
            helper.ConsoleCommands.Add("bf_go_tile", "BotFramework: 导航到指定地点+tile。用法：bf_go_tile <locationName> <x> <y>", this.CmdGoTile);
            helper.ConsoleCommands.Add("bf_go_home", "BotFramework: 导航回家到床边。用法：bf_go_home", this.CmdGoHome);
            helper.ConsoleCommands.Add("bf_stop", "BotFramework: 停止导航。用法：bf_stop", this.CmdStop);
            helper.ConsoleCommands.Add("bf_nav_debug", "BotFramework: 输出当前导航状态。用法：bf_nav_debug", this.CmdNavDebug);
        }

        private void onLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.config = this.Helper.ReadConfig<ModConfig>();

            LogProxy.SetDebug(this.config.DebugEnvironment);
            LogProxy.SetMonitor(this.Monitor);

            this._navigator = new PlayerNavigator(this.Helper, this.Monitor);
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            // ignore if player hasn't loaded a save yet
            if (!Context.IsWorldReady)
                return;

            if (e.Button == SButton.U)
            {
                this.Monitor.Log($"{Game1.player.Name} pressed {e.Button}.", LogLevel.Debug);
                WaterBotTest bot = new WaterBotTest();

                bot.Start();
            }
        }

        private void CmdGo(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("世界未就绪（请先进入存档）。", LogLevel.Warn);
                return;
            }
            if (this._navigator == null)
                this._navigator = new PlayerNavigator(this.Helper, this.Monitor);

            if (args.Length < 1)
            {
                this.Monitor.Log("用法：bf_go <locationName>", LogLevel.Info);
                return;
            }

            string locationName = string.Join(" ", args);
            try
            {
                this._navigator.GoToLocation(locationName);
            }
            catch (System.Exception ex)
            {
                this.Monitor.Log($"导航失败：{ex.Message}", LogLevel.Error);
            }
        }

        private void CmdGoTile(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("世界未就绪（请先进入存档）。", LogLevel.Warn);
                return;
            }
            if (this._navigator == null)
                this._navigator = new PlayerNavigator(this.Helper, this.Monitor);

            if (args.Length < 3)
            {
                this.Monitor.Log("用法：bf_go_tile <locationName> <x> <y>", LogLevel.Info);
                return;
            }

            if (!int.TryParse(args[^2], out int x) || !int.TryParse(args[^1], out int y))
            {
                this.Monitor.Log("x/y 必须是整数。", LogLevel.Warn);
                return;
            }

            string locationName = string.Join(" ", args.Take(args.Length - 2));
            try
            {
                this._navigator.GoToLocation(locationName, new Point(x, y));
            }
            catch (System.Exception ex)
            {
                this.Monitor.Log($"导航失败：{ex.Message}", LogLevel.Error);
            }
        }

        private void CmdGoHome(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("世界未就绪（请先进入存档）。", LogLevel.Warn);
                return;
            }
            if (this._navigator == null)
                this._navigator = new PlayerNavigator(this.Helper, this.Monitor);

            try
            {
                this._navigator.GoHomeToBed();
            }
            catch (System.Exception ex)
            {
                this.Monitor.Log($"导航失败：{ex.Message}", LogLevel.Error);
            }
        }

        private void CmdStop(string command, string[] args)
        {
            if (this._navigator == null)
                return;
            this._navigator.Stop();
        }

        private void CmdNavDebug(string command, string[] args)
        {
            if (this._navigator == null)
            {
                this.Monitor.Log("Navigator 未初始化。", LogLevel.Info);
                return;
            }
            this.Monitor.Log($"Navigator.IsNavigating={this._navigator.IsNavigating}, location={Game1.player.currentLocation?.NameOrUniqueName}, tile=({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y}), controller={(Game1.player.controller == null ? "null" : Game1.player.controller.GetType().Name)}", LogLevel.Info);
        }
    }
}