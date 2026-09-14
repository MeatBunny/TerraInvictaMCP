using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace TerraInvictaMCP
{
    public static class Main
    {
        public static UnityModManager.ModEntry.ModLogger Log;

        // The difficulty windows skip their swap off the main thread rather than
        // risk interleaved swap and restore pairs.
        public static int MainThreadId;

        static Harmony harmony;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Log = modEntry.Logger;
            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUnload = OnUnload;
            // The listener comes up first, and every patch is applied with
            // warn-and-skip. A game update that renames one of these signatures
            // must not take the harness down with it: an unpatched bridge still
            // answers every other verb and can report what broke, while a dead
            // listener leaves every tool saying "bridge down" and nothing saying
            // why.
            StartServer();
            harmony = new Harmony(modEntry.Info.Id);
            AiControl.Install(harmony);
            return true;
        }

        // A reload replaces this assembly. Anything it still holds dies with it:
        // an engagement would leave a faction flagged AI with nothing left that
        // knows how to unflag it, and the patches would keep calling into methods
        // that no longer exist.
        static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            try { AiControl.Release("released on unload"); } catch (Exception) { }
            try { Server.Stop(); }
            catch (Exception e) { Server.LastError = e.Message; }
            if (harmony != null)
            {
                harmony.UnpatchAll(harmony.Id);
                harmony = null;
            }
            return true;
        }

        // A bind failure leaves the mod inert: no listener, no drain work, one log line.
        static void StartServer()
        {
            try
            {
                // Start returns false when a listener is already open, so a re-toggle
                // does not log a second time.
                if (Server.Start()) Log.Log("Listening on 127.0.0.1:" + Server.Port);
            }
            catch (Exception e)
            {
                Server.LastError = e.Message;
                Log.Log("Inert, listener failed: " + e.Message);
            }
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value) StartServer();
            else
            {
                // Releasing first: a mod switched off while engaged would leave the
                // faction AI-driven with no listener left to take it back. Saves
                // stay safe either way, but that is a trap, not a design.
                try { AiControl.Release("released on unload"); } catch (Exception) { }
                try { Server.Stop(); }
                catch (Exception e) { Server.LastError = e.Message; }
            }
            return true;
        }

        static void OnUpdate(UnityModManager.ModEntry modEntry, float delta)
        {
            try { Server.Drain(); }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
            // Separate catch: a failing run_until check must not stop the queue draining.
            try { Verbs.Tick(); }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Endpoint: 127.0.0.1:" + Server.Port
                + (Server.Running ? "  (listening)" : "  (down)"));
            GUILayout.Label("Connections: " + Server.ConnectionCount);
            GUILayout.Label("Last error: " + (Server.LastError.Length == 0 ? "none" : Server.LastError));
        }
    }
}
