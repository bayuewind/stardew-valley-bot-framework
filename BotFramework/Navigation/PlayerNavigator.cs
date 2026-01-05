using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BotFramework.Navigation
{
    /// <summary>
    /// Drives the local player (Farmer) to a destination across the world by:
    /// - building a location-to-location route via warps
    /// - walking to each warp tile using PathFindController
    /// - continuing after each warp until the destination is reached
    /// </summary>
    public sealed class PlayerNavigator : IDisposable
    {
        public const string BuildTag = "player-navigator:v3";
        private static readonly PathFindController.endBehavior NoopEndBehavior = () => { };

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;

        private bool _active;
        private Queue<string> _route; // location keys to visit next (excluding current)
        private string _targetLocationKey;
        private Point? _targetTile;
        private int _finalFacingDirection;

        private Point? _currentStepTarget;
        private bool _currentStepIsWarp;
        private string _currentStepNextLocationKey;
        private int _stuckTicks;
        private Point _lastTile;

        public bool IsNavigating => _active;

        public PlayerNavigator(IModHelper helper, IMonitor monitor)
        {
            this._helper = helper ?? throw new ArgumentNullException(nameof(helper));
            this._monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

            this._monitor.Log($"[Navigator] Init ({BuildTag})", LogLevel.Info);
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Player.Warped += this.OnWarped;
        }

        public void Dispose()
        {
            this._helper.Events.GameLoop.UpdateTicked -= this.OnUpdateTicked;
            this._helper.Events.Player.Warped -= this.OnWarped;
        }

        /// <summary>
        /// Navigate to a location (optionally a tile inside it). The location name is resolved using fuzzy search.
        /// </summary>
        public void GoToLocation(string locationNameOrUniqueName, Point? targetTile = null, int finalFacingDirection = 2)
        {
            if (!Context.IsWorldReady)
                throw new InvalidOperationException("World is not ready.");

            GameLocation target = Utility.fuzzyLocationSearch(locationNameOrUniqueName);
            if (target == null)
                throw new ArgumentException($"Location '{locationNameOrUniqueName}' not found.", nameof(locationNameOrUniqueName));

            string fromKey = this.GetLocationKey(Game1.player.currentLocation);
            string toKey = this.GetLocationKey(target);

            List<string> path = this.BuildLocationRoute(fromKey, toKey);
            if (path == null || path.Count == 0)
                throw new InvalidOperationException($"Unable to build a route from '{fromKey}' to '{toKey}'.");

            // Exclude current location, we only queue the next hops.
            this._route = new Queue<string>(path.Skip(1));
            this._targetLocationKey = toKey;
            this._targetTile = targetTile;
            this._finalFacingDirection = finalFacingDirection;
            this._active = true;
            this._currentStepTarget = null;
            this._currentStepIsWarp = false;
            this._currentStepNextLocationKey = null;
            this._stuckTicks = 0;
            this._lastTile = Game1.player.TilePoint;

            this._monitor.Log($"[Navigator] GoToLocation: {fromKey} -> {toKey}" + (targetTile.HasValue ? $" @ ({targetTile.Value.X},{targetTile.Value.Y})" : ""), LogLevel.Info);
            this.NavigateNextStep();
        }

        /// <summary>
        /// Navigate to the player's bed spot in their FarmHouse (fallback to bed tile if bed spot is unavailable).
        /// </summary>
        public void GoHomeToBed(int finalFacingDirection = 2)
        {
            if (!Context.IsWorldReady)
                throw new InvalidOperationException("World is not ready.");

            var home = Utility.getHomeOfFarmer(Game1.player) as FarmHouse;
            if (home == null)
                throw new InvalidOperationException("Unable to resolve player's home (FarmHouse).");

            Point bedTarget = this.GetBestBedTargetTile(home);
            this.GoToLocation(home.NameOrUniqueName, bedTarget, finalFacingDirection);
        }

        public void Stop()
        {
            if (!this._active)
                return;

            this._monitor.Log("[Navigator] Stopped.", LogLevel.Info);
            this._active = false;
            this._route = null;
            this._targetLocationKey = null;
            this._targetTile = null;
            this._currentStepTarget = null;
            this._currentStepIsWarp = false;
            this._currentStepNextLocationKey = null;
            this._stuckTicks = 0;
        }

        private void OnWarped(object sender, WarpedEventArgs e)
        {
            if (!this._active)
                return;
            if (!e.IsLocalPlayer)
                return;

            if (this._route == null || this._route.Count == 0)
            {
                // We may still need to walk to the final target tile inside the destination.
                this.NavigateNextStep();
                return;
            }

            string newKey = this.GetLocationKey(e.NewLocation);
            if (this.KeysEqual(this._route.Peek(), newKey))
            {
                this._route.Dequeue();
                this._monitor.Log($"[Navigator] Warped into {newKey}. Remaining hops: {this._route.Count}", LogLevel.Info);
                this._currentStepIsWarp = false;
                this._currentStepNextLocationKey = null;
                this.NavigateNextStep();
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!this._active)
                return;
            if (!Context.IsWorldReady)
                return;

            // Completion check: at destination location and (optionally) at target tile.
            string curKey = this.GetLocationKey(Game1.player.currentLocation);
            if (this._targetLocationKey != null && this.KeysEqual(curKey, this._targetLocationKey))
            {
                if (!this._targetTile.HasValue)
                {
                    this._monitor.Log($"[Navigator] Arrived at location {curKey}.", LogLevel.Info);
                    this.Stop();
                    return;
                }

                Point curTile = Game1.player.TilePoint;
                if (this.WithinRange(curTile, this._targetTile.Value, 0))
                {
                    Game1.player.faceDirection(this._finalFacingDirection);
                    this._monitor.Log($"[Navigator] Arrived at target tile ({this._targetTile.Value.X},{this._targetTile.Value.Y}) in {curKey}.", LogLevel.Info);
                    this.Stop();
                    return;
                }
            }

            // Stuck detection: if we have an active controller but player isn't changing tiles, retry.
            Point nowTile = Game1.player.TilePoint;
            if (nowTile == this._lastTile)
                this._stuckTicks++;
            else
                this._stuckTicks = 0;
            this._lastTile = nowTile;

            // If controller finished or got cleared, keep progressing.
            if (Game1.player.controller == null)
            {
                this.NavigateNextStep();
                return;
            }

            // Retry if stuck for ~2 seconds (120 ticks).
            if (this._stuckTicks >= 120 && this._currentStepTarget.HasValue)
            {
                this._monitor.Log($"[Navigator] Detected stuck near ({this._currentStepTarget.Value.X},{this._currentStepTarget.Value.Y}) (warpStep={this._currentStepIsWarp}), retrying...", LogLevel.Warn);
                this._stuckTicks = 0;
                this.RetryCurrentStep();
            }
        }

        private void NavigateNextStep()
        {
            if (!this._active)
                return;

            // If we're in the destination location and have a tile target, walk to it.
            string curKey = this.GetLocationKey(Game1.player.currentLocation);
            if (this._targetLocationKey != null && this.KeysEqual(curKey, this._targetLocationKey))
            {
                if (this._targetTile.HasValue)
                {
                    this._currentStepIsWarp = false;
                    this._currentStepNextLocationKey = null;
                    this.StartPathToTile(Game1.player.currentLocation, this._targetTile.Value, onEnd: null);
                }
                return;
            }

            // If there are no more hops but we're not yet in destination, rebuild route from current location.
            if (this._route == null || this._route.Count == 0)
            {
                if (this._targetLocationKey == null)
                {
                    this.Stop();
                    return;
                }

                string fromKey = this.GetLocationKey(Game1.player.currentLocation);
                List<string> path = this.BuildLocationRoute(fromKey, this._targetLocationKey);
                if (path == null || path.Count == 0)
                {
                    this._monitor.Log($"[Navigator] Unable to rebuild route from '{fromKey}' to '{this._targetLocationKey}'.", LogLevel.Error);
                    this.Stop();
                    return;
                }

                this._route = new Queue<string>(path.Skip(1));
            }

            string nextKey = this._route.Peek();
            GameLocation currentLoc = Game1.player.currentLocation;
            Point? warpOrigin = this.FindWarpOriginTile(currentLoc, nextKey);
            if (!warpOrigin.HasValue)
            {
                // Sometimes TargetName is Name/UniqueName mismatch; try fuzzy resolving and matching again.
                GameLocation resolvedNext = Utility.fuzzyLocationSearch(nextKey);
                if (resolvedNext != null)
                    warpOrigin = this.FindWarpOriginTile(currentLoc, this.GetLocationKey(resolvedNext));
            }

            if (!warpOrigin.HasValue)
            {
                this._monitor.Log($"[Navigator] No warp from '{this.GetLocationKey(currentLoc)}' to '{nextKey}'. Rebuilding route...", LogLevel.Warn);
                this._route = null;
                return;
            }

            // IMPORTANT:
            // Some warps are triggered by walking *off the map boundary* (warp coords can be outside map bounds).
            // In those cases, clamping into the map will cause the player to stop one tile short and never warp.
            // So we intentionally use the raw warp coordinates here (matching the working behavior in your original code).
            this._currentStepIsWarp = true;
            this._currentStepNextLocationKey = nextKey;
            this._monitor.Log($"[Navigator] Step: {this.GetLocationKey(currentLoc)} -> {nextKey} via warp tile ({warpOrigin.Value.X},{warpOrigin.Value.Y})", LogLevel.Info);
            this.StartPathToTile(currentLoc, warpOrigin.Value, onEnd: null);
        }

        private void RetryCurrentStep()
        {
            if (!this._currentStepTarget.HasValue)
                return;

            GameLocation loc = Game1.player.currentLocation;
            Point origin = this._currentStepTarget.Value;

            // For warp steps, do NOT "helpfully" choose a neighbor tile, or we can end up one tile short forever.
            if (this._currentStepIsWarp)
            {
                this._monitor.Log($"[Navigator] Warp-step retry: re-path to warp tile ({origin.X},{origin.Y}) -> {this._currentStepNextLocationKey}", LogLevel.Warn);
                this.StartPathToTile(loc, origin, onEnd: null);
                return;
            }

            foreach (Point candidate in this.GetNeighborCandidates(origin))
            {
                if (candidate == origin)
                    continue;
                if (this.IsTilePassable(loc, candidate))
                {
                    this.StartPathToTile(loc, candidate, onEnd: null);
                    return;
                }
            }

            // Give up on alternate tile; try recompute route.
            this._route = null;
        }

        private void StartPathToTile(GameLocation location, Point targetTile, PathFindController.endBehavior onEnd)
        {
            if (location == null)
                return;

            this._currentStepTarget = targetTile;
            // Match the working pattern from the user's original code:
            // always provide a non-null end behavior delegate.
            PathFindController.endBehavior endBehavior = onEnd ?? NoopEndBehavior;

            // Clear any previous controller and stop current movement before assigning a new controller.
            Game1.player.controller = null;
            Game1.player.Halt();
            Game1.player.controller = new PathFindController(Game1.player, location, targetTile, this._finalFacingDirection, endBehavior);
        }

        private Point GetBestBedTargetTile(FarmHouse home)
        {
            var bed = home.furniture.OfType<BedFurniture>().FirstOrDefault();
            if (bed == null)
                throw new InvalidOperationException("No bed furniture found in FarmHouse.");

            Point? spot = bed.GetBedSpot();
            if (spot.HasValue)
                return spot.Value;

            // Fallback: bed's placement tile.
            Vector2 bedTile = bed.TileLocation;
            return new Point((int)bedTile.X, (int)bedTile.Y);
        }

        private Vector2? FindWarpOriginTileVector(GameLocation location, string nextLocationKey)
        {
            foreach (var w in location.warps)
            {
                if (this.KeysEqual(w.TargetName, nextLocationKey))
                    return new Vector2(w.X, w.Y);

                // Also accept matching the destination's NameOrUniqueName/Name via fuzzy search.
                GameLocation resolved = Utility.fuzzyLocationSearch(nextLocationKey);
                if (resolved != null && (w.TargetName == resolved.NameOrUniqueName || w.TargetName == resolved.Name))
                    return new Vector2(w.X, w.Y);
            }
            return null;
        }

        private Point? FindWarpOriginTile(GameLocation location, string nextLocationKey)
        {
            Vector2? v = this.FindWarpOriginTileVector(location, nextLocationKey);
            if (!v.HasValue)
                return null;
            return new Point((int)v.Value.X, (int)v.Value.Y);
        }

        private Point FindReachableNear(GameLocation location, Point origin)
        {
            foreach (Point candidate in this.GetNeighborCandidates(origin))
            {
                if (this.IsTilePassable(location, candidate))
                    return candidate;
            }
            return origin;
        }

        private IEnumerable<Point> GetNeighborCandidates(Point p)
        {
            // Prefer the origin, then 4-neighborhood, then diagonals.
            yield return p;
            yield return new Point(p.X + 1, p.Y);
            yield return new Point(p.X - 1, p.Y);
            yield return new Point(p.X, p.Y + 1);
            yield return new Point(p.X, p.Y - 1);
            yield return new Point(p.X + 1, p.Y + 1);
            yield return new Point(p.X + 1, p.Y - 1);
            yield return new Point(p.X - 1, p.Y + 1);
            yield return new Point(p.X - 1, p.Y - 1);
        }

        private bool IsTilePassable(GameLocation location, Point tile)
        {
            if (location == null)
                return false;
            if (tile.X < 0 || tile.Y < 0)
                return false;

            // Avoid out-of-bounds for map sizes when available.
            try
            {
                int width = location.Map?.Layers?[0]?.LayerWidth ?? int.MaxValue;
                int height = location.Map?.Layers?[0]?.LayerHeight ?? int.MaxValue;
                if (tile.X >= width || tile.Y >= height)
                    return false;
            }
            catch
            {
                // ignore
            }

            Rectangle rect = new Rectangle(tile.X * 64 + 1, tile.Y * 64 + 1, 62, 62);
            bool colliding = location.isCollidingPosition(rect, Game1.viewport, isFarmer: true, -1, glider: false, Game1.player);
            return !colliding;
        }

        private bool WithinRange(Point a, Point b, int range)
        {
            return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) <= range;
        }

        private string GetLocationKey(GameLocation location)
        {
            return location?.NameOrUniqueName ?? "";
        }

        private bool KeysEqual(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private List<string> BuildLocationRoute(string fromKey, string toKey)
        {
            // Build a mapping from both Name and NameOrUniqueName to the same location.
            var nameToLoc = new Dictionary<string, GameLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (GameLocation l in Game1.locations)
            {
                if (l == null)
                    continue;
                if (!string.IsNullOrEmpty(l.Name))
                    nameToLoc[l.Name] = l;
                if (!string.IsNullOrEmpty(l.NameOrUniqueName))
                    nameToLoc[l.NameOrUniqueName] = l;
            }

            // Normalize endpoints via fuzzy search if needed.
            if (!nameToLoc.ContainsKey(fromKey))
            {
                GameLocation fromLoc = Utility.fuzzyLocationSearch(fromKey);
                if (fromLoc != null)
                    fromKey = this.GetLocationKey(fromLoc);
            }
            if (!nameToLoc.ContainsKey(toKey))
            {
                GameLocation toLoc = Utility.fuzzyLocationSearch(toKey);
                if (toLoc != null)
                    toKey = this.GetLocationKey(toLoc);
            }

            if (!nameToLoc.ContainsKey(fromKey) || !nameToLoc.ContainsKey(toKey))
                return new List<string>();

            var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = new Queue<string>();

            q.Enqueue(fromKey);
            visited.Add(fromKey);

            while (q.Count > 0)
            {
                string curKey = q.Dequeue();
                if (this.KeysEqual(curKey, toKey))
                    break;

                GameLocation loc = nameToLoc[curKey];
                foreach (var w in loc.warps)
                {
                    string nextKey = w.TargetName;
                    if (string.IsNullOrEmpty(nextKey))
                        continue;

                    // Ensure the target exists in our map; fallback to fuzzy search for cases like "FarmHouse" etc.
                    if (!nameToLoc.ContainsKey(nextKey))
                    {
                        GameLocation resolved = Utility.fuzzyLocationSearch(nextKey);
                        if (resolved != null)
                        {
                            if (!string.IsNullOrEmpty(resolved.Name))
                                nameToLoc[resolved.Name] = resolved;
                            if (!string.IsNullOrEmpty(resolved.NameOrUniqueName))
                                nameToLoc[resolved.NameOrUniqueName] = resolved;
                            nextKey = this.GetLocationKey(resolved);
                        }
                    }

                    if (!nameToLoc.ContainsKey(nextKey))
                        continue;

                    if (!visited.Contains(nextKey))
                    {
                        visited.Add(nextKey);
                        prev[nextKey] = curKey;
                        q.Enqueue(nextKey);
                    }
                }
            }

            if (!visited.Contains(toKey))
                return new List<string>();

            var path = new List<string>();
            string node = toKey;
            while (true)
            {
                path.Add(node);
                if (!prev.ContainsKey(node))
                    break;
                node = prev[node];
            }
            path.Reverse();
            return path;
        }
    }
}

