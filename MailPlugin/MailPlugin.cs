// MailPlugin - by matheo mercier
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.Utils;
using Newtonsoft.Json;

namespace MailPlugin
{
    // ===================== DONNEES =====================
    public class Mail
    {
        public int Id;
        public int FromId;
        public string FromName;
        public int ToId;
        public string Subject;
        public string Body;
        public string Date;
        public bool Read;
    }

    public class MailDb
    {
        public int NextId = 1;
        public List<Mail> Mails = new List<Mail>();
        // Tous les joueurs deja connectes (CharacterId -> Nom) : permet d'ecrire a un joueur hors ligne
        public Dictionary<int, string> Players = new Dictionary<int, string>();
        // Joueurs bloques (ne peuvent plus envoyer de mails)
        public HashSet<int> Blocked = new HashSet<int>();
    }

    // ===================== PLUGIN =====================
    // Herite de ModKit.ModKit (comme AAMenu) pour pouvoir s'enregistrer dans AAMenu.
    public class MailMain : ModKit.ModKit
    {
        private static readonly object dbLock = new object();
        private static MailDb db = new MailDb();
        private static string savePath;

        public MailMain(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "matheo mercier");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            savePath = Path.Combine(pluginsPath, "MailPlugin_data.json");
            Load();

            // ----- Menu interaction (AAMenu) : ligne visible par tout le monde -----
            AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Boite mail", ui =>
            {
                Player p = PanelHelper.ReturnPlayerFromPanel(ui);
                if (p == null) return;
                p.ClosePanel(ui);
                OpenMain(p);
            });

            // ----- Menu administration (AAMenu) : le 1er int = niveau admin requis (a ajuster) -----
            AAMenu.Menu.AddAdminTabLine(PluginInformations, 1, "Messagerie", ui =>
            {
                Player p = PanelHelper.ReturnPlayerFromPanel(ui);
                if (p == null) return;
                p.ClosePanel(ui);
                OpenAdmin(p);
            });

            // Commande de secours (le cast evite l'ambiguite entre les surcharges du constructeur)
            SChatCommand command = new SChatCommand("/mail", "Ouvrir la messagerie", "/mail",
                (Action<Player, string[]>)((player, args) => OpenMain(player)));
            command.Register();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        // Enregistre chaque joueur a sa connexion pour qu'il apparaisse dans la liste
        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            if (player?.character == null) return;

            db.Players[player.character.Id] = player.FullName;
            Save();

            int unread = Unread(player.character.Id);
            if (unread > 0)
                Notif(player, "Messagerie", $"Vous avez {unread} mail(s) non lu(s)", NotificationManager.Type.Info);
        }

        // ===================== OUTILS =====================
        private static void Notif(Player p, string title, string msg, NotificationManager.Type type)
            => p.Notify(title, msg, type, 5f);

        private static void Load()
        {
            try
            {
                if (File.Exists(savePath))
                    db = JsonConvert.DeserializeObject<MailDb>(File.ReadAllText(savePath)) ?? new MailDb();
            }
            catch (Exception e) { Console.WriteLine("[MailPlugin] Erreur load: " + e.Message); }

            // Un fichier incomplet peut laisser des listes a null
            if (db.Mails == null) db.Mails = new List<Mail>();
            if (db.Players == null) db.Players = new Dictionary<int, string>();
            if (db.Blocked == null) db.Blocked = new HashSet<int>();
            if (db.Mails.Count > 0 && db.NextId <= db.Mails.Max(m => m.Id))
                db.NextId = db.Mails.Max(m => m.Id) + 1;
        }

        private static void Save()
        {
            try
            {
                lock (dbLock)
                {
                    // Ecriture dans un fichier temporaire puis remplacement : evite un JSON corrompu en cas de crash
                    string tmp = savePath + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(db, Formatting.Indented));
                    if (File.Exists(savePath)) File.Delete(savePath);
                    File.Move(tmp, savePath);
                }
            }
            catch (Exception e) { Console.WriteLine("[MailPlugin] Erreur save: " + e.Message); }
        }

        private static int Unread(int charId) => db.Mails.Count(m => m.ToId == charId && !m.Read);

        private static bool IsAdmin(Player p) => p.IsAdmin;

        // ===================== MENU PRINCIPAL =====================
        public static void OpenMain(Player player)
        {
            if (player?.character == null) return;
            int me = player.character.Id;
            var panel = new UIPanel("Messagerie", UIPanel.PanelType.Tab);

            panel.AddTabLine($"Ma boite mail ({Unread(me)} non lu)", ui => { player.ClosePanel(ui); OpenInbox(player); });
            panel.AddTabLine("Creer un mail", ui => { player.ClosePanel(ui); OpenPlayerList(player); });
            if (IsAdmin(player))
                panel.AddTabLine("<color=#ff5555>Administration</color>", ui => { player.ClosePanel(ui); OpenAdmin(player); });

            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        // ===================== CREER UN MAIL =====================
        private static void OpenPlayerList(Player player)
        {
            if (db.Blocked.Contains(player.character.Id))
            {
                Notif(player, "Messagerie", "Vous etes bloque, impossible d'envoyer un mail.", NotificationManager.Type.Error);
                OpenMain(player);
                return;
            }

            var panel = new UIPanel("Choisir un destinataire", UIPanel.PanelType.Tab);
            var players = db.Players.Where(x => x.Key != player.character.Id).OrderBy(x => x.Value).ToList();

            if (players.Count == 0) panel.AddTabLine("Aucun joueur", ui => { });
            foreach (var p in players)
            {
                int toId = p.Key; string toName = p.Value;
                panel.AddTabLine(toName, ui => { player.ClosePanel(ui); AskSubject(player, toId, toName); });
            }
            panel.AddButton("Choisir", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => { player.ClosePanel(ui); OpenMain(player); });
            player.ShowPanelUI(panel);
        }

        private static void AskSubject(Player player, int toId, string toName)
        {
            var panel = new UIPanel($"Mail a {toName}", UIPanel.PanelType.Input);
            panel.SetText("Entrez l'objet du mail");
            panel.SetInputPlaceholder("Objet...");
            panel.AddButton("Suivant", ui =>
            {
                string subject = ui.inputText?.Trim();
                if (string.IsNullOrEmpty(subject)) { Notif(player, "Messagerie", "Objet vide.", NotificationManager.Type.Warning); return; }
                player.ClosePanel(ui);
                AskBody(player, toId, toName, subject);
            });
            panel.AddButton("Annuler", ui => { player.ClosePanel(ui); OpenMain(player); });
            player.ShowPanelUI(panel);
        }

        private static void AskBody(Player player, int toId, string toName, string subject)
        {
            var panel = new UIPanel($"Mail a {toName}", UIPanel.PanelType.Input);
            panel.SetText("Entrez le message");
            panel.SetInputPlaceholder("Message...");
            panel.AddButton("Envoyer", ui =>
            {
                string body = ui.inputText?.Trim();
                if (string.IsNullOrEmpty(body)) { Notif(player, "Messagerie", "Message vide.", NotificationManager.Type.Warning); return; }
                player.ClosePanel(ui);
                SendMail(player, toId, subject, body);
                OpenMain(player);
            });
            panel.AddButton("Annuler", ui => { player.ClosePanel(ui); OpenMain(player); });
            player.ShowPanelUI(panel);
        }

        private static void SendMail(Player from, int toId, string subject, string body)
        {
            if (db.Blocked.Contains(from.character.Id))
            {
                Notif(from, "Messagerie", "Vous etes bloque.", NotificationManager.Type.Error);
                return;
            }

            db.Mails.Add(new Mail
            {
                Id = db.NextId++,
                FromId = from.character.Id,
                FromName = from.FullName,
                ToId = toId,
                Subject = subject,
                Body = body,
                Date = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                Read = false
            });
            Save();
            Notif(from, "Messagerie", "Mail envoye !", NotificationManager.Type.Success);

            // Previent le destinataire s'il est en ligne
            var target = Nova.server.GetAllInGamePlayers().FirstOrDefault(p => p.character != null && p.character.Id == toId);
            if (target != null)
                Notif(target, "Messagerie", $"Nouveau mail de {from.FullName}", NotificationManager.Type.Info);
        }

        // ===================== BOITE MAIL =====================
        private static void OpenInbox(Player player)
        {
            int me = player.character.Id;
            var panel = new UIPanel("Ma boite mail", UIPanel.PanelType.Tab);
            var mails = db.Mails.Where(m => m.ToId == me).OrderByDescending(m => m.Id).ToList();

            if (mails.Count == 0) panel.AddTabLine("Aucun mail", ui => { });
            foreach (var m in mails)
            {
                var mail = m;
                string label = (mail.Read ? "" : "<color=#55ff55>[NEW]</color> ") + $"{mail.FromName} - {mail.Subject}";
                panel.AddTabLine(label, ui => { player.ClosePanel(ui); ReadMail(player, mail, false); });
            }
            panel.AddButton("Lire", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => { player.ClosePanel(ui); OpenMain(player); });
            player.ShowPanelUI(panel);
        }

        private static void ReadMail(Player viewer, Mail mail, bool adminView)
        {
            if (!adminView && mail.ToId == viewer.character.Id && !mail.Read) { mail.Read = true; Save(); }

            var panel = new UIPanel(mail.Subject, UIPanel.PanelType.Text);
            panel.SetText($"De : {mail.FromName}\nDate : {mail.Date}\n\n{mail.Body}");
            panel.AddButton("Supprimer", ui =>
            {
                db.Mails.RemoveAll(x => x.Id == mail.Id);
                Save();
                viewer.ClosePanel(ui);
                if (adminView) OpenAdminMailbox(viewer, mail.ToId); else OpenInbox(viewer);
            });
            panel.AddButton("Retour", ui =>
            {
                viewer.ClosePanel(ui);
                if (adminView) OpenAdminMailbox(viewer, mail.ToId); else OpenInbox(viewer);
            });
            viewer.ShowPanelUI(panel);
        }

        // ===================== ADMINISTRATION =====================
        private static void OpenAdmin(Player admin)
        {
            if (!IsAdmin(admin)) return;
            var panel = new UIPanel("Administration - Messagerie", UIPanel.PanelType.Tab);
            panel.AddTabLine("Gerer les boites mail", ui => { admin.ClosePanel(ui); OpenAdminPlayers(admin, false); });
            panel.AddTabLine("Bloquer / debloquer un utilisateur", ui => { admin.ClosePanel(ui); OpenAdminPlayers(admin, true); });
            panel.AddButton("Selectionner", ui => ui.SelectTab());
            panel.AddButton("Fermer", ui => admin.ClosePanel(ui));
            admin.ShowPanelUI(panel);
        }

        private static void OpenAdminPlayers(Player admin, bool blockMode)
        {
            if (!IsAdmin(admin)) return;
            var panel = new UIPanel(blockMode ? "Bloquer un utilisateur" : "Choisir une boite mail", UIPanel.PanelType.Tab);
            var players = db.Players.OrderBy(x => x.Value).ToList();

            if (players.Count == 0) panel.AddTabLine("Aucun joueur", ui => { });
            foreach (var p in players)
            {
                int id = p.Key; string name = p.Value;
                string state = db.Blocked.Contains(id) ? " <color=#ff5555>[BLOQUE]</color>" : "";
                panel.AddTabLine(name + state, ui =>
                {
                    if (blockMode)
                    {
                        if (!db.Blocked.Remove(id)) db.Blocked.Add(id);
                        Save();
                        admin.ClosePanel(ui);
                        OpenAdminPlayers(admin, true);
                    }
                    else
                    {
                        admin.ClosePanel(ui);
                        OpenAdminMailbox(admin, id);
                    }
                });
            }
            panel.AddButton(blockMode ? "Bloquer / Debloquer" : "Ouvrir", ui => ui.SelectTab());
            panel.AddButton("Retour", ui => { admin.ClosePanel(ui); OpenAdmin(admin); });
            admin.ShowPanelUI(panel);
        }

        private static void OpenAdminMailbox(Player admin, int targetId)
        {
            if (!IsAdmin(admin)) return;
            string name = db.Players.TryGetValue(targetId, out string n) ? n : "Inconnu";
            var panel = new UIPanel($"Boite de {name}", UIPanel.PanelType.Tab);
            var mails = db.Mails.Where(m => m.ToId == targetId).OrderByDescending(m => m.Id).ToList();

            if (mails.Count == 0) panel.AddTabLine("Aucun mail", ui => { });
            foreach (var m in mails)
            {
                var mail = m;
                panel.AddTabLine($"{mail.FromName} - {mail.Subject}", ui => { admin.ClosePanel(ui); ReadMail(admin, mail, true); });
            }
            panel.AddButton("Lire", ui => ui.SelectTab());
            panel.AddButton("Vider la boite", ui =>
            {
                db.Mails.RemoveAll(x => x.ToId == targetId);
                Save();
                admin.ClosePanel(ui);
                OpenAdminMailbox(admin, targetId);
            });
            panel.AddButton("Retour", ui => { admin.ClosePanel(ui); OpenAdminPlayers(admin, false); });
            admin.ShowPanelUI(panel);
        }
    }
}
