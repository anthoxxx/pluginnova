using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace PNJCreator
{
    /// <summary>
    /// Composant Unity du plugin : exécute sur le thread principal les résultats des requêtes IA,
    /// fait avancer les patrouilles et sauvegarde mémoire / consommation de façon différée.
    /// </summary>
    public class NpcRunner : MonoBehaviour
    {
        public PNJCreator Plugin;
        readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        float nextSave;
        bool spawned;
        float readySince = -1f;

        public void RunOnMainThread(Action action) => mainThread.Enqueue(action);

        void Update()
        {
            if (Plugin == null) return;

            while (mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError("[PNJCreator] " + e); }
            }

            // Apparition des PNJ une fois le serveur et la carte prêts.
            if (!spawned)
            {
                if (!NpcManager.ServerReady) { readySince = -1f; return; }
                if (readySince < 0f) readySince = Time.time;
                if (Time.time - readySince < Plugin.Store.Config.General.SpawnDelaySeconds) return;
                spawned = true;
                Plugin.Npcs.SpawnAll();
            }

            try { Plugin.Npcs.Tick(Time.deltaTime, Time.time); }
            catch (Exception e) { Debug.LogError("[PNJCreator] Patrouille : " + e); }

            if (Time.time >= nextSave)
            {
                nextSave = Time.time + 10f;
                if (Plugin.Store.MemoryDirty) Plugin.Store.SaveMemory();
                if (Plugin.Store.UsageDirty) Plugin.Store.SaveUsage();
            }
        }

        void OnApplicationQuit()
        {
            if (Plugin == null) return;
            if (Plugin.Store.MemoryDirty) Plugin.Store.SaveMemory();
            if (Plugin.Store.UsageDirty) Plugin.Store.SaveUsage();
        }
    }
}
