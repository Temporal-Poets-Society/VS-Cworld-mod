using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;

namespace CylinderWorld
{
    /// <summary>
    /// The one place every Harmony patch and every reflected non-public game member goes through, so a game update that
    /// renames or changes one of them switches off only the feature that needs it, with one log line saying what is
    /// missing, instead of crashing the game or leaving a feature half-patched.
    ///
    /// Use: create one per feature, resolve every type, method and field through it, queue the patches, then Apply. Apply
    /// patches nothing if anything was missing; if a patch fails part-way (or a transpiler calls Off because its code
    /// pattern isn't there), the patches it had applied are removed again. At runtime a patch body that throws calls
    /// Fail, which logs once and turns the feature off; patch bodies return early while On is false. A lookup never
    /// throws: a member that can't be told apart any more (a game update added an overload) counts as missing.
    /// </summary>
    internal sealed class GameFeature
    {
        public readonly string Name;
        readonly Harmony harmony;
        readonly ILogger log;
        readonly string source;
        readonly List<string> missing = new();
        readonly List<(MethodBase target, HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod transpiler, HarmonyMethod finalizer)> queued = new();
        readonly List<(MethodBase target, MethodInfo patch)> applied = new();
        string offReason;
        volatile bool on;
        int reported;

        /// <param name="name">What the player loses when it is off, in plain words ("seam edit mirroring").</param>
        /// <param name="harmony">The feature's Harmony instance (null for a feature that only reflects).</param>
        /// <param name="source">Whose members it reads, for the log line when one is missing: the game's, or another
        /// mod's ("the installed Earth").</param>
        public GameFeature(string name, Harmony harmony, ILogger log, string source = "this game version")
        {
            Name = name;
            this.harmony = harmony;
            this.log = log;
            this.source = source;
        }

        /// <summary>Applied and not failed since.</summary>
        public bool On => on;

        public Type Type(string fullName)
        {
            Type t = null;
            string why = Lookup(() => t = AccessTools.TypeByName(fullName));
            if (t == null) missing.Add("type " + fullName + why);
            return t;
        }

        /// <summary>A declared or inherited method by name (and parameter types, when overloaded).</summary>
        public MethodInfo Method(Type type, string name, Type[] args = null)
        {
            MethodInfo m = null;
            string why = type == null ? "" : Lookup(() => m = AccessTools.Method(type, name, args));
            if (m == null) missing.Add("method " + (type?.Name ?? "?") + "." + name + why);
            return m;
        }

        /// <summary>A field by name; with <paramref name="fieldType"/>, its type must be assignable to that type, so a
        /// changed type is reported here instead of failing a cast later.</summary>
        public FieldInfo Field(Type type, string name, Type fieldType = null)
        {
            FieldInfo f = null;
            string why = type == null ? "" : Lookup(() => f = AccessTools.Field(type, name));
            if (f == null || fieldType != null && !fieldType.IsAssignableFrom(f.FieldType))
            {
                missing.Add("field " + (type?.Name ?? "?") + "." + name + (f == null ? why : " (now " + f.FieldType.Name + ")"));
                return null;
            }
            return f;
        }

        public PropertyInfo Property(Type type, string name)
        {
            PropertyInfo p = null;
            string why = type == null ? "" : Lookup(() => p = AccessTools.Property(type, name));
            if (p == null) missing.Add("property " + (type?.Name ?? "?") + "." + name + why);
            return p;
        }

        /// <summary>Runs one lookup; "" when it returned, else why it threw. Harmony's lookups by name alone throw
        /// AmbiguousMatchException once a second member of that name exists, which only this feature may pay for.</summary>
        static string Lookup(Action find)
        {
            try { find(); return ""; }
            catch (AmbiguousMatchException) { return " (now overloaded)"; }
            catch (Exception e) { return " (" + e.GetType().Name + ": " + e.Message + ")"; }
        }

        /// <summary>Any other precondition (a delegate that compiled, a value of the expected type).</summary>
        public bool Require(bool condition, string what)
        {
            if (!condition) missing.Add(what);
            return condition;
        }

        public GameFeature Patch(MethodBase target, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (target == null) missing.Add("a patch target");
            else queued.Add((target, prefix, postfix, transpiler, finalizer));
            return this;
        }

        /// <summary>For a transpiler that doesn't find its pattern. While Apply patches, the feature is undone. Once it is
        /// on, Harmony runs the transpiler again whenever any mod patches that method: the method then keeps the code the
        /// transpiler returned unchanged, so the feature is off from then on, with one log line, as after Fail.</summary>
        public void Off(string reason)
        {
            if (on)
            {
                on = false;
                Report(reason);
            }
            else offReason ??= reason;
        }

        /// <summary>Patches everything queued, or nothing. False (and one warning) when a target is missing or a patch failed.</summary>
        public bool Apply()
        {
            if (missing.Count > 0)
            {
                Report(source + " has no " + string.Join(", ", missing));
                return false;
            }
            try
            {
                foreach (var (target, prefix, postfix, transpiler, finalizer) in queued)
                {
                    harmony.Patch(target, prefix, postfix, transpiler, finalizer);
                    foreach (var h in new[] { prefix, postfix, transpiler, finalizer })
                        if (h?.method != null) applied.Add((target, h.method));
                    if (offReason != null) break;
                }
            }
            catch (Exception e)
            {
                offReason ??= e.GetType().Name + ": " + e.Message;
            }
            if (offReason != null)
            {
                Unpatch();
                Report(offReason);
                return false;
            }
            on = true;
            return true;
        }

        /// <summary>A patch body threw at runtime: switch the feature off and log it once.</summary>
        public void Fail(Exception e)
        {
            on = false;
            Report("it failed: " + e);
        }

        /// <summary>Removes this feature's patches (also on Dispose).</summary>
        public void Unpatch()
        {
            on = false;
            foreach (var (target, patch) in applied)
            {
                try { harmony.Unpatch(target, patch); }
                catch (Exception e) { log.Warning("Cylinder World: could not remove a patch of {0}: {1}", Name, e.Message); }
            }
            applied.Clear();
        }

        void Report(string why)
        {
            if (Interlocked.Exchange(ref reported, 1) == 0)
                log.Warning("Cylinder World: {0} is off: {1}", Name, why);
        }
    }
}
