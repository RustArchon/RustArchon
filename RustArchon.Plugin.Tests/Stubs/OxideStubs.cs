// Copyright ©2026 Scott Blomfield

// Minimal stand-ins for the slice of the Oxide/Carbon/Rust API the plugin uses, so the plugin source can be
// compiled and exercised in an ordinary test project. They prove OUR code is well formed and behaves; they
// say nothing about the real framework's restrictions or behavior - that is what the live smoke test on a real
// server is for. Kept at C# 7.3 syntax because RustArchon.Plugin.CompileCheck compiles this file too.

using System;
using System.Collections.Generic;

namespace Oxide.Core
{
    public struct VersionNumber
    {
        private readonly string _text;
        public VersionNumber(string text) { _text = text; }
        public override string ToString() { return _text ?? "0.0.0"; }
    }
}

// Mirrors the real Carbon/Oxide types the plugin reads to see whether another plugin is loaded (checked against Carbon.Common.dll on the live server's
// files: Plugin.Name and Version come from its base, IsLoaded is on Plugin, and RustPlugin.plugins is an Oxide.Core.Libraries.Plugins with Find/Exists).
namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public string Name = "";

        // Real plugins get this from the framework; the tests use whatever the plugin's [Info] says.
        public Oxide.Core.VersionNumber Version = new Oxide.Core.VersionNumber("0.1.0");

        public bool IsLoaded = true;
    }
}

namespace Oxide.Core.Libraries
{
    public class Plugins
    {
        // Test helper, not part of the real API: the plugins this stand-in says are loaded, by name.
        public readonly Dictionary<string, Oxide.Core.Plugins.Plugin> Loaded = new Dictionary<string, Oxide.Core.Plugins.Plugin>();

        public Oxide.Core.Plugins.Plugin Find(string name)
        {
            Oxide.Core.Plugins.Plugin found;
            return Loaded.TryGetValue(name, out found) ? found : null;
        }

        public bool Exists(string name) { return Loaded.ContainsKey(name); }
    }
}

namespace Oxide.Plugins
{
    public class InfoAttribute : Attribute
    {
        public InfoAttribute(string title, string author, string version) { }
    }

    public class DescriptionAttribute : Attribute
    {
        public DescriptionAttribute(string description) { }
    }

    public class ConsoleCommandAttribute : Attribute
    {
        public ConsoleCommandAttribute(string name) { Name = name; }
        public string Name { get; private set; }
    }

    // Stand-ins for Oxide's timer. Every() only records that a repeating callback was requested; the tests drive the
    // callback themselves (calling the plugin's Tick), so they control time rather than wait for it.
    public class Timer
    {
        public bool Destroyed;
        public void Destroy() { Destroyed = true; }
    }

    public class PluginTimers
    {
        public readonly List<Timer> Started = new List<Timer>();

        public Timer Every(float seconds, Action callback)
        {
            var timer = new Timer();
            Started.Add(timer);
            return timer;
        }

        // Once() records the request and the delay; tests run the callback themselves.
        public readonly List<KeyValuePair<float, Action>> Once_ = new List<KeyValuePair<float, Action>>();

        public Timer Once(float seconds, Action callback)
        {
            Once_.Add(new KeyValuePair<float, Action>(seconds, callback));
            var timer = new Timer();
            Started.Add(timer);
            return timer;
        }
    }

    public class RustPlugin : Oxide.Core.Plugins.Plugin
    {
        public readonly List<string> Log = new List<string>();

        protected PluginTimers timer = new PluginTimers();

        // The framework's registry of loaded plugins (the real field is an Oxide.Core.Libraries.Plugins, with Find and Exists). Public here only
        // so a test can say which plugins are loaded.
        public readonly Oxide.Core.Libraries.Plugins plugins = new Oxide.Core.Libraries.Plugins();

        protected void Puts(string message) { Log.Add(message); }
        // Recorded so tests can see which hooks are switched on: "+Hook" for Subscribe, "-Hook" for Unsubscribe.
        public readonly List<string> Subscriptions = new List<string>();

        protected void Subscribe(string hook) { Subscriptions.Add("+" + hook); }
        protected void Unsubscribe(string hook) { Subscriptions.Add("-" + hook); }
    }
}

// Mirrors the real type deliberately: on the game server ConsoleSystem.Arg.Args is a StringView[], NOT a string[]
// (found on the first live load, when code written against a string[] stub did not compile there). Keeping this
// faithful means code that mishandles it fails to compile here too.
public struct StringView
{
    private readonly string _value;
    public StringView(string value) { _value = value; }
    public override string ToString() { return _value ?? ""; }
}

public class ConsoleSystem
{
    // The console's own entry point (the real one is Run(Option, string, params object[]) and returns the output).
    public struct Option
    {
        public static Option Server { get { return new Option(); } }
    }

    public static string Run(Option option, string command, params object[] args) { return ""; }

    // What a real ConsoleSystem.Arg exposes that the plugin touches. Connection is null for RCON and the
    // server console, non-null for a player's in-game console.
    public class Arg
    {
        public object Connection;
        public StringView[] Args;
        public readonly List<string> Replies = new List<string>();

        // Test helper, not part of the real API: builds an Arg carrying these arguments.
        public static Arg WithArgs(params string[] args)
        {
            var arg = new Arg();
            if (args.Length > 0)
            {
                arg.Args = new StringView[args.Length];
                for (var i = 0; i < args.Length; i++) { arg.Args[i] = new StringView(args[i]); }
            }
            return arg;
        }

        public void ReplyWith(string message) { Replies.Add(message); }

        public bool HasArgs(int minimum = 1) { return Args != null && Args.Length >= minimum; }

        public string GetString(int index, string defaultValue = "")
        {
            return Args != null && index < Args.Length ? Args[index].ToString() : defaultValue;
        }
    }
}

// Stand-ins for the handful of Rust and Unity types the damage hooks read. Faithful to the member names and types the
// plugin uses on the real game (HitInfo.InitiatorPlayer is a BasePlayer, damageTypes.Total() a float, and so on), so a
// misuse fails to compile here; whether the real game's classes behave the same is what the live server shows.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static float Distance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x; var dy = a.y - b.y; var dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    public class Transform { public Vector3 position; }

    public struct Quaternion { public Vector3 eulerAngles; }

    // The real one searches the whole scene; the plugin only ever calls it with MonumentInfo, and tests replace that call.
    public class Object
    {
        public static T[] FindObjectsOfType<T>() where T : class { return new T[0]; }
    }

    public class Component
    {
        private static int _nextInstanceId = 1000;
        private readonly int _instanceId = System.Threading.Interlocked.Increment(ref _nextInstanceId);

        public Transform transform = new Transform();

        public int GetInstanceID() { return _instanceId; }
    }
}

namespace Rust
{
    public enum DamageType { Generic, Bullet, Slash, Blunt, Bleeding, Fall, Cold, Heat, Radiation, Hunger, Thirst, Drowned }
}

public class DamageTypeList
{
    public float TotalValue;
    public Rust.DamageType Majority;
    public float Total() { return TotalValue; }
    public Rust.DamageType GetMajorityDamageType() { return Majority; }
}

// The game's entity base. serverEntities is the world's list of every entity; the tests fill it, and a test can also
// change it mid-scan to see what the plugin does when the real list moves under it.
public class BaseNetworkable : UnityEngine.Component
{
    public string ShortPrefabName = "";
    public bool IsDestroyed;

    public static readonly EntityRealm serverEntities = new EntityRealm();
}

public class EntityRealm : IEnumerable<BaseNetworkable>
{
    public readonly List<BaseNetworkable> All = new List<BaseNetworkable>();

    public IEnumerator<BaseNetworkable> GetEnumerator() { return All.GetEnumerator(); }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
}

public class BaseEntity : BaseNetworkable { public ulong OwnerID; }

// The world, the terrain's monuments and the console, as on the game server (verified against its Assembly-CSharp and
// Rust.Localization). Tests set Size/Seed and the monument list.
public static class World
{
    public static uint Size;
    public static uint Seed;
}

public class Phrase { public string english { get; set; } }

public class MonumentInfo : UnityEngine.Component
{
    public bool shouldDisplayOnMap = true;
    public Phrase displayPhrase;
}



public class BaseCombatEntity : BaseEntity { }

// A tool cupboard. The real class name really is spelled "Privlidge".
public class BuildingPrivlidge : BaseCombatEntity
{
    // Mirrors the real field: the game stores only the ids (verified against the dedicated server's Assembly-CSharp).
    public HashSet<ulong> authorizedPlayers = new HashSet<ulong>();
}

// The look direction, as on the game's PlayerEyes (verified against the dedicated server's Assembly-CSharp).
public class PlayerEyes { public UnityEngine.Quaternion rotation; }

public class BasePlayer : BaseCombatEntity
{
    public ulong userID;
    public string displayName = "";
    public PlayerEyes eyes = new PlayerEyes();

    // The connected players, as the game's BasePlayer.activePlayerList; tests fill and clear it.
    public static readonly List<BasePlayer> activePlayerList = new List<BasePlayer>();

    // The players "in the world" for FindAwakeOrSleepingByID; tests fill and clear it.
    public static readonly Dictionary<ulong, BasePlayer> InWorld = new Dictionary<ulong, BasePlayer>();

    public static BasePlayer FindAwakeOrSleepingByID(ulong id)
    {
        BasePlayer found;
        return InWorld.TryGetValue(id, out found) ? found : null;
    }
}

public class HitInfo
{
    public BaseEntity Initiator;
    public BasePlayer InitiatorPlayer;
    public BaseEntity WeaponPrefab;
    public bool isHeadshot;
    public DamageTypeList damageTypes = new DamageTypeList();
}
