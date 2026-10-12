using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        readonly List<KeyValuePair<float, Action>> delayed = new List<KeyValuePair<float, Action>>();
        float nextSave;
        float nextObserverCheck;
        bool spawned;
        float readySince = -1f;

        public void RunOnMainThread(Action action) => mainThread.Enqueue(action);

        /// <summary>Exécute une action sur le thread principal dans <paramref name="seconds"/> secondes.</summary>
        public void RunAfter(float seconds, Action action) =>
            mainThread.Enqueue(() => delayed.Add(new KeyValuePair<float, Action>(Time.time + seconds, action)));

        void Update()
        {
            if (Plugin == null) return;

            while (mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError("[PNJCreator] " + e); }
            }

            for (int i = delayed.Count - 1; i >= 0; i--)
            {
                if (Time.time < delayed[i].Key) continue;
                Action action = delayed[i].Value;
                delayed.RemoveAt(i);
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

            // Un joueur vient de voir un PNJ (connexion, approche) : on lui renvoie l'apparence
            // un peu après, le temps que son client ait fini de créer le personnage.
            if (Time.time >= nextObserverCheck)
            {
                nextObserverCheck = Time.time + 1f;
                foreach (var npc in Plugin.Npcs.NpcsWithNewObservers())
                {
                    var data = npc;
                    RunAfter(1.5f, () => Plugin.Npcs.ApplySkin(data));
                    RunAfter(6f, () => Plugin.Npcs.ApplySkin(data));
                }
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
