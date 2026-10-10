// PermisArmePlugin - FICHIER UNIQUE (aucune dépendance à d'autres .cs)
// using System.IO volontairement absent : "Path" est ambigu avec InsaneSystems.RoadNavigator.Path
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace PermisArmePlugin
{
    // ===================== MODELES =====================
    public class ExamQuestion
    {
        public string Texte { get; set; }
        public List<string> Reponses { get; set; } = new List<string>();
        public int BonneReponse { get; set; }
    }

    public class PermisMessages
    {
        public string PermisDejaPossede { get; set; } = "Vous possédez déjà un permis de port d'arme.";
        public string PasAssezArgent { get; set; } = "Il vous faut {prix}€ pour passer l'examen.";
        public string DelaiAttente { get; set; } = "Vous devez attendre encore {minutes} min.";
        public string Reussite { get; set; } = "Félicitations ! Note {note}/{total}. Permis délivré.";
        public string Echec { get; set; } = "Échec : {note}/{total} (minimum {min}).";
        public string PermisRetire { get; set; } = "Votre permis de port d'arme a été retiré.";
    }

    public class PermisConfig
    {
        public int PrixPermis { get; set; } = 17000;
        public int NoteMinimale { get; set; } = 8;
        public int NombreQuestions { get; set; } = 10;
        public bool MelangerQuestions { get; set; } = true;
        public bool MelangerReponses { get; set; } = true;
        public int DelaiNouvelEssaiMinutes { get; set; } = 30;
        public int NiveauAdminMin { get; set; } = 1;
        public List<string> ServicesPolice { get; set; } = new List<string> { "LawEnforcement", "Police" };
        // Taille du texte dans les menus, en % de la taille normale (évite que les lignes se chevauchent)
        public int TailleTexte { get; set; } = 70;
        // Nombre de caractères max par ligne avant retour à la ligne (question de l'examen)
        public int CaracteresParLigne { get; set; } = 55;
        public PermisMessages Messages { get; set; } = new PermisMessages();
        public List<ExamQuestion> Questions { get; set; } = QuestionsDeBase();

        public static List<ExamQuestion> QuestionsDeBase()
        {
            return new List<ExamQuestion>
            {
                new ExamQuestion { Texte = "Que devez-vous faire avant de manipuler une arme ?", Reponses = new List<string> { "Vérifier qu'elle est sécurisée et suivre les règles de sécurité", "La pointer vers quelqu'un", "Appuyer sur la détente pour tester" }, BonneReponse = 0 },
                new ExamQuestion { Texte = "Dans quelle direction devez-vous garder le canon ?", Reponses = new List<string> { "Vers une personne", "Dans une direction sûre", "Vers vos pieds" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Que faites-vous si vous trouvez une arme dont vous ne connaissez pas l'état ?", Reponses = new List<string> { "Vous la manipulez immédiatement", "Vous la laissez en sécurité et demandez l'aide d'une personne qualifiée", "Vous la prêtez à un ami" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Comment devez-vous conserver une arme lorsqu'elle n'est pas utilisée ?", Reponses = new List<string> { "Accessible à tout le monde", "Dans un endroit sécurisé, inaccessible aux personnes non autorisées", "Sur une table" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Qui peut utiliser votre arme ?", Reponses = new List<string> { "N'importe quel ami", "Uniquement une personne autorisée conformément aux règles applicables", "Tous les visiteurs" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Que devez-vous faire avant de transporter une arme ?", Reponses = new List<string> { "Respecter les règles de transport et vérifier qu'elle est sécurisée", "La laisser chargée et visible", "La donner à un passager" }, BonneReponse = 0 },
                new ExamQuestion { Texte = "Quand est-il acceptable de pointer une arme vers une personne ?", Reponses = new List<string> { "Pour plaisanter", "Pour impressionner quelqu'un", "Jamais, sauf situation légalement justifiée et conformément à la formation reçue" }, BonneReponse = 2 },
                new ExamQuestion { Texte = "Que faites-vous si vous n'êtes pas certain de la loi concernant votre permis ?", Reponses = new List<string> { "Vous ignorez les règles", "Vous vérifiez les règles auprès d'une source officielle ou des autorités compétentes", "Vous demandez à un inconnu" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Que devez-vous faire si vous constatez un problème de sécurité avec votre arme ?", Reponses = new List<string> { "Continuer à l'utiliser", "Arrêter de l'utiliser et demander l'aide d'un professionnel qualifié", "La laisser à un enfant" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Le permis de port d'arme autorise-t-il à menacer quelqu'un ?", Reponses = new List<string> { "Oui, en cas de dispute", "Non, le permis ne justifie ni les menaces ni un usage illégal", "Oui, pour obtenir ce que l'on veut" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Que faites-vous si une personne non autorisée veut prendre votre arme ?", Reponses = new List<string> { "Vous la lui laissez", "Vous sécurisez l'arme et empêchez l'accès sans vous mettre en danger", "Vous la posez sans surveillance" }, BonneReponse = 1 },
                new ExamQuestion { Texte = "Pourquoi faut-il respecter les consignes des autorités et la réglementation ?", Reponses = new List<string> { "Pour assurer la sécurité et respecter la loi", "Uniquement pour éviter une amende", "Ce n'est pas nécessaire" }, BonneReponse = 0 }
            };
        }
    }

    public class PermisEntry
    {
        public int CharacterId { get; set; }
        public string Nom { get; set; }
        public string DateObtention { get; set; }
    }

    // ===================== STOCKAGE =====================
    public static class PermisStore
    {
        public static string Dir;
        static string ConfigPath { get { return System.IO.Path.Combine(Dir, "config.json"); } }
        static string PermisPath { get { return System.IO.Path.Combine(Dir, "permis.json"); } }
        public static PermisConfig Config = new PermisConfig();
        public static List<PermisEntry> Liste = new List<PermisEntry>();
        static readonly object _lock = new object();

        public static void Init(string dir)
        {
            Dir = dir;
            System.IO.Directory.CreateDirectory(Dir);
            LoadConfig();
            LoadPermis();
        }

        public static void LoadConfig()
        {
            lock (_lock)
            {
                if (!System.IO.File.Exists(ConfigPath))
                {
                    Config = new PermisConfig();
                    SaveConfig();
                    return;
                }
                Config = JsonConvert.DeserializeObject<PermisConfig>(System.IO.File.ReadAllText(ConfigPath)) ?? new PermisConfig();
                // Les anciennes configurations peuvent contenir une liste de questions vide.
                if (Config.Questions == null || Config.Questions.Count == 0)
                {
                    Config.Questions = PermisConfig.QuestionsDeBase();
                    SaveConfig();
                }
            }
        }

        public static void SaveConfig()
        {
            lock (_lock) System.IO.File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
        }

        public static void LoadPermis()
        {
            lock (_lock)
            {
                if (!System.IO.File.Exists(PermisPath)) { Liste = new List<PermisEntry>(); return; }
                Liste = JsonConvert.DeserializeObject<List<PermisEntry>>(System.IO.File.ReadAllText(PermisPath)) ?? new List<PermisEntry>();
            }
        }

        public static void SavePermis()
        {
            lock (_lock) System.IO.File.WriteAllText(PermisPath, JsonConvert.SerializeObject(Liste, Formatting.Indented));
        }

        public static bool APermis(int characterId) { return Liste.Any(p => p.CharacterId == characterId); }

        public static void Ajouter(int characterId, string nom)
        {
            if (APermis(characterId)) return;
            Liste.Add(new PermisEntry { CharacterId = characterId, Nom = nom, DateObtention = DateTime.Now.ToString("dd/MM/yyyy HH:mm") });
            SavePermis();
        }

        public static bool Retirer(int characterId)
        {
            int n = Liste.RemoveAll(p => p.CharacterId == characterId);
            if (n > 0) SavePermis();
            return n > 0;
        }
    }

    // ===================== PONT VERS LE SDK =====================
    public static class NovaBridge
    {
        public static int CharacterId(Player p) { return p.character.Id; }
        public static string FullName(Player p) { return p.FullName; }
        public static int Money(Player p) { return (int)p.Money; }
        public static void RemoveMoney(Player p, int amount, string reason) { p.AddMoney(-amount, reason); }

        // Texte réduit (balise <size>) pour que les lignes ne se chevauchent pas
        public static string Petit(string s) { return "<size=" + PermisStore.Config.TailleTexte + "%>" + s + "</size>"; }

        // Coupe un texte long en plusieurs lignes de "max" caractères
        public static List<string> Couper(string texte, int max)
        {
            var lignes = new List<string>();
            var cur = "";
            foreach (var mot in (texte ?? "").Split(' '))
            {
                if (cur.Length > 0 && cur.Length + 1 + mot.Length > max) { lignes.Add(cur); cur = mot; }
                else cur = cur.Length == 0 ? mot : cur + " " + mot;
            }
            if (cur.Length > 0) lignes.Add(cur);
            return lignes;
        }

        public static bool IsPolice(Player p, List<string> servicesPolice)
        {
            var noms = Activites(p);
            return noms.Any(n => servicesPolice.Any(s =>
                !string.IsNullOrWhiteSpace(n) &&
                !string.IsNullOrWhiteSpace(s) &&
                (string.Equals(s, n, StringComparison.OrdinalIgnoreCase) ||
                 n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)));
        }

        // Noms des secteurs d'activité du joueur (ex: LawEnforcement)
        public static List<string> Activites(Player p)
        {
            var res = new List<string>();
            try
            {
                foreach (var a in (System.Collections.IEnumerable)p.GetActivities())
                    if (a != null) res.Add(a.ToString());
            }
            catch (Exception) { }
            return res;
        }

        public static void Notify(Player p, string msg, bool success = true)
        {
            p.Notify("Permis d'arme", msg, success ? NotificationManager.Type.Success : NotificationManager.Type.Error);
        }

        public static void Show(Player p, UIPanel panel) { p.ShowPanelUI(panel); }
        public static void Close(Player p, UIPanel panel) { p.ClosePanel(panel); }
        public static IEnumerable<Player> OnlinePlayers() { return Nova.server.Players; }

        public static void RegisterAdminMenu(PluginInformations infos, int minLevel, Action<Player> open, Func<UIPanel, Player> getPlayer)
        {
            AAMenu.Menu.AddAdminPluginTabLine(infos, minLevel, "Permis d'arme (config)", ui => open(getPlayer(ui)));
        }
    }

    // ===================== PLUGIN =====================
    public class PermisArme : ModKit.ModKit
    {
        public static PermisArme Instance;
        static readonly System.Random Rng = new System.Random();
        static readonly Dictionary<int, DateTime> DernierEchec = new Dictionary<int, DateTime>();

        public PermisArme(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.1.0", "Alfred");
            Instance = this;
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            string dll = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            PermisStore.Init(System.IO.Path.Combine(dll, "PermisArme"));

            // Points bleus natifs ModKit, placés par le staff :
            // AAMenu > Administration > Points bleus > Type "Permis d'arme"
            Orm.RegisterTable<ExamPattern>();
            ExamPattern pattern = new ExamPattern(false) { Context = this };
            PointHelper.AddPattern(nameof(ExamPattern), pattern);
            if (AAMenu.AAMenu.menu != null)
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Permis d'arme", pattern, this);
            else
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez /permisarmepoint.");

            Func<UIPanel, Player> getPlayer = ui => this.PanelHelper.ReturnPlayerFromPanel(ui);
            NovaBridge.RegisterAdminMenu(PluginInformations, PermisStore.Config.NiveauAdminMin, AdminMenu.Ouvrir, getPlayer);

            new SChatCommand("/permisarme", "Registre des permis d'arme (police)", "/permisarme", (player, args) => OuvrirPolice(player)).Register();
            new SChatCommand("/monpermisarme", "Consulter votre permis de port d'arme", "/monpermisarme", (player, args) => MonPermis(player)).Register();
            new SChatCommand("/permisarmepoint", "Placer les points d'examen (staff)", "/permisarmepoint", (player, args) => OuvrirPoints(player)).Register();

            // AAMenu > Documents : le joueur consulte son permis
            AAMenu.Menu.AddDocumentTabLine(PluginInformations, "Mon permis de port d'arme", ui => MonPermis(getPlayer(ui)));
            // AAMenu > Métier (forces de l'ordre) : registre / retrait des permis
            AAMenu.Menu.AddBizTabLine(PluginInformations, new List<Activity.Type> { Activity.Type.LawEnforcement }, null,
                "Registre permis d'arme", ui => OuvrirPolice(getPlayer(ui)));

            Logger.LogSuccess(PluginInformations.SourceName, "Permis d'arme chargé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            // Affiche au joueur tous les points bleus enregistrés
            PointHelper.InitAllNPoint(player);
        }

        public static void OuvrirPoints(Player staff)
        {
            if (!staff.IsAdmin || !staff.serviceAdmin)
            {
                NovaBridge.Notify(staff, "Vous devez être staff et en service admin.", false);
                return;
            }
            new ExamPattern(false) { Context = Instance }.CreateOrGenerate(staff);
        }

        public static void OuvrirPolice(Player player)
        {
            if (player == null || player.character == null) return;
            if (!NovaBridge.IsPolice(player, PermisStore.Config.ServicesPolice))
            {
                string info = player.IsAdmin ? " (vos activités : " + string.Join(", ", NovaBridge.Activites(player)) + ")" : "";
                NovaBridge.Notify(player, "Réservé à la police." + info, false); return;
            }
            PoliceMenu.Ouvrir(player);
        }

        public static void MonPermis(Player player)
        {
            if (player == null || player.character == null) return;
            PermisStore.LoadPermis();
            var permis = PermisStore.Liste.FirstOrDefault(x => x.CharacterId == NovaBridge.CharacterId(player));
            var panel = new UIPanel("Mon permis d'arme", UIPanel.PanelType.Text);
            panel.SetText(NovaBridge.Petit(permis == null
                ? "Vous ne possédez pas de permis de port d'arme."
                : "Titulaire : " + permis.Nom + "\nDate d'obtention : " + permis.DateObtention));
            panel.AddButton("Fermer", ui => NovaBridge.Close(player, panel));
            NovaBridge.Show(player, panel);
        }

        static string Fmt(string s, string k1, object v1, string k2 = null, object v2 = null, string k3 = null, object v3 = null)
        {
            s = s.Replace("{" + k1 + "}", Convert.ToString(v1));
            if (k2 != null) s = s.Replace("{" + k2 + "}", Convert.ToString(v2));
            if (k3 != null) s = s.Replace("{" + k3 + "}", Convert.ToString(v3));
            return s;
        }

        // ---------- EXAMEN ----------
        public static class Examen
        {
            class Session { public List<ExamQuestion> Qs; public int Index; public int Score; }

            public static void Proposer(Player p)
            {
                if (p == null || p.character == null) return;
                var cfg = PermisStore.Config;
                int id = NovaBridge.CharacterId(p);

                if (PermisStore.APermis(id)) { NovaBridge.Notify(p, cfg.Messages.PermisDejaPossede, false); return; }
                DateTime t;
                if (DernierEchec.TryGetValue(id, out t))
                {
                    double reste = (t.AddMinutes(cfg.DelaiNouvelEssaiMinutes) - DateTime.Now).TotalMinutes;
                    if (reste > 0) { NovaBridge.Notify(p, Fmt(cfg.Messages.DelaiAttente, "minutes", Math.Ceiling(reste)), false); return; }
                }
                if (NovaBridge.Money(p) < cfg.PrixPermis)
                { NovaBridge.Notify(p, Fmt(cfg.Messages.PasAssezArgent, "prix", cfg.PrixPermis), false); return; }

                var panel = new UIPanel("Permis de port d'arme", UIPanel.PanelType.Text);
                panel.SetText(NovaBridge.Petit("Examen : " + cfg.NombreQuestions + " questions\nMinimum : " + cfg.NoteMinimale + " bonnes réponses\nPrix (débité si réussite) : " + cfg.PrixPermis + "€"));
                panel.AddButton("Commencer", ui => { NovaBridge.Close(p, panel); Demarrer(p); });
                panel.AddButton("Annuler", ui => NovaBridge.Close(p, panel));
                NovaBridge.Show(p, panel);
            }

            static void Demarrer(Player p)
            {
                var cfg = PermisStore.Config;
                var qs = cfg.Questions.ToList();
                if (cfg.MelangerQuestions) qs = qs.OrderBy(x => Rng.Next()).ToList();
                qs = qs.Take(Math.Min(cfg.NombreQuestions, qs.Count)).ToList();
                if (qs.Count == 0) { NovaBridge.Notify(p, "Aucune question configurée.", false); return; }
                Poser(p, new Session { Qs = qs });
            }

            static void Poser(Player p, Session s)
            {
                if (s.Index >= s.Qs.Count) { Terminer(p, s); return; }
                var cfg = PermisStore.Config;
                var q = s.Qs[s.Index];
                var reps = q.Reponses.Select((txt, i) => new KeyValuePair<string, bool>(txt, i == q.BonneReponse)).ToList();
                if (cfg.MelangerReponses) reps = reps.OrderBy(x => Rng.Next()).ToList();

                // Titre court : la question est affichée dans les premières lignes (petite taille, coupée)
                var panel = new UIPanel("Question " + (s.Index + 1) + "/" + s.Qs.Count, UIPanel.PanelType.Tab);
                foreach (var ligne in NovaBridge.Couper(q.Texte, cfg.CaracteresParLigne))
                    panel.AddTabLine(NovaBridge.Petit("<color=#f0c040>" + ligne + "</color>"), ui => { });

                char lettre = 'A';
                foreach (var r in reps)
                {
                    bool bonne = r.Value;
                    panel.AddTabLine(NovaBridge.Petit(lettre + ") " + r.Key), ui =>
                    {
                        if (bonne) s.Score++;
                        s.Index++;
                        NovaBridge.Close(p, panel);
                        Poser(p, s);
                    });
                    lettre++;
                }
                panel.AddButton("Valider", ui => ui.SelectTab());
                panel.AddButton("Abandonner", ui => { NovaBridge.Close(p, panel); DernierEchec[NovaBridge.CharacterId(p)] = DateTime.Now; });
                NovaBridge.Show(p, panel);
            }

            static void Terminer(Player p, Session s)
            {
                var cfg = PermisStore.Config;
                int id = NovaBridge.CharacterId(p);
                bool reussi = s.Score >= cfg.NoteMinimale;

                if (reussi && NovaBridge.Money(p) < cfg.PrixPermis)
                { NovaBridge.Notify(p, Fmt(cfg.Messages.PasAssezArgent, "prix", cfg.PrixPermis), false); return; }

                if (reussi)
                {
                    NovaBridge.RemoveMoney(p, cfg.PrixPermis, "Permis de port d'arme");
                    PermisStore.Ajouter(id, NovaBridge.FullName(p));
                    DernierEchec.Remove(id);
                    NovaBridge.Notify(p, Fmt(cfg.Messages.Reussite, "note", s.Score, "total", s.Qs.Count), true);
                }
                else
                {
                    DernierEchec[id] = DateTime.Now;
                    NovaBridge.Notify(p, Fmt(cfg.Messages.Echec, "note", s.Score, "total", s.Qs.Count, "min", cfg.NoteMinimale), false);
                }
            }
        }

        // ---------- POLICE ----------
        public static class PoliceMenu
        {
            public static void Ouvrir(Player police)
            {
                PermisStore.LoadPermis();
                var panel = new UIPanel("Registre permis d'arme", UIPanel.PanelType.Tab);
                if (PermisStore.Liste.Count == 0) panel.AddTabLine(NovaBridge.Petit("(aucun permis délivré)"), ui => { });
                foreach (var pm in PermisStore.Liste.ToList())
                {
                    var cur = pm;
                    panel.AddTabLine(NovaBridge.Petit(cur.Nom + " - " + cur.DateObtention), ui =>
                    {
                        NovaBridge.Close(police, panel);
                        Confirmer(police, cur);
                    });
                }
                panel.AddButton("Sélectionner", ui => ui.SelectTab());
                panel.AddButton("Fermer", ui => NovaBridge.Close(police, panel));
                NovaBridge.Show(police, panel);
            }

            static void Confirmer(Player police, PermisEntry pm)
            {
                var panel = new UIPanel("Retirer le permis ?", UIPanel.PanelType.Text);
                panel.SetText(NovaBridge.Petit(pm.Nom + "\nObtenu le " + pm.DateObtention));
                panel.AddButton("Retirer le permis", ui =>
                {
                    bool retire = PermisStore.Retirer(pm.CharacterId);
                    if (retire)
                    {
                        NovaBridge.Notify(police, "Permis de " + pm.Nom + " retiré.");
                        var cible = NovaBridge.OnlinePlayers().FirstOrDefault(x => x != null && x.character != null && NovaBridge.CharacterId(x) == pm.CharacterId);
                        if (cible != null) NovaBridge.Notify(cible, PermisStore.Config.Messages.PermisRetire, false);
                    }
                    else
                    {
                        NovaBridge.Notify(police, "Ce permis n'est plus dans le registre.", false);
                    }
                    NovaBridge.Close(police, panel);
                    Ouvrir(police);
                });
                panel.AddButton("Retour", ui => { NovaBridge.Close(police, panel); Ouvrir(police); });
                NovaBridge.Show(police, panel);
            }
        }

        // ---------- MENU AA (STAFF) ----------
        public static class AdminMenu
        {
            public static void Ouvrir(Player staff)
            {
                var c = PermisStore.Config;
                var panel = new UIPanel("Permis d'arme - Config", UIPanel.PanelType.Tab);
                panel.AddTabLine(NovaBridge.Petit("Prix du permis : " + c.PrixPermis + "€"), ui => { NovaBridge.Close(staff, panel); Saisir(staff, "Prix du permis (€)", v => c.PrixPermis = v); });
                panel.AddTabLine(NovaBridge.Petit("Note minimale : " + c.NoteMinimale), ui => { NovaBridge.Close(staff, panel); Saisir(staff, "Note minimale", v => c.NoteMinimale = v); });
                panel.AddTabLine(NovaBridge.Petit("Questions par examen : " + c.NombreQuestions), ui => { NovaBridge.Close(staff, panel); Saisir(staff, "Nombre de questions", v => c.NombreQuestions = v); });
                panel.AddTabLine(NovaBridge.Petit("Délai nouvel essai : " + c.DelaiNouvelEssaiMinutes + " min"), ui => { NovaBridge.Close(staff, panel); Saisir(staff, "Délai (minutes)", v => c.DelaiNouvelEssaiMinutes = v); });
                panel.AddTabLine(NovaBridge.Petit("Taille du texte : " + c.TailleTexte + "%"), ui => { NovaBridge.Close(staff, panel); Saisir(staff, "Taille du texte (%)", v => c.TailleTexte = Math.Max(30, Math.Min(100, v))); });
                panel.AddTabLine(NovaBridge.Petit("Points bleus d'examen (placer / gérer)"), ui => { NovaBridge.Close(staff, panel); OuvrirPoints(staff); });
                panel.AddTabLine(NovaBridge.Petit("Registre des permis (" + PermisStore.Liste.Count + ")"), ui => { NovaBridge.Close(staff, panel); PoliceMenu.Ouvrir(staff); });
                panel.AddTabLine(NovaBridge.Petit("Recharger config.json"), ui => { PermisStore.LoadConfig(); NovaBridge.Notify(staff, "Config rechargée."); });
                panel.AddButton("Sélectionner", ui => ui.SelectTab());
                panel.AddButton("Fermer", ui => NovaBridge.Close(staff, panel));
                NovaBridge.Show(staff, panel);
            }

            static void Saisir(Player staff, string titre, Action<int> set)
            {
                var panel = new UIPanel(titre, UIPanel.PanelType.Input);
                panel.SetInputPlaceholder("Entrez un nombre");
                panel.AddButton("Valider", ui =>
                {
                    int v;
                    if (int.TryParse(ui.inputText, out v) && v >= 0)
                    {
                        set(v); PermisStore.SaveConfig();
                        NovaBridge.Notify(staff, "Enregistré.");
                        NovaBridge.Close(staff, panel); Ouvrir(staff);
                    }
                    else NovaBridge.Notify(staff, "Nombre invalide.", false);
                });
                panel.AddButton("Annuler", ui => { NovaBridge.Close(staff, panel); Ouvrir(staff); });
                NovaBridge.Show(staff, panel);
            }
        }
    }

    // ===================== POINT BLEU NATIF (ModKit / AAMenu) =====================
    /// <summary>
    /// Modèle de point d'examen. Le staff le place via AAMenu > Administration > Points bleus
    /// (type "Permis d'arme") ou /permisarmepoint. Le joueur qui entre dans le point bleu
    /// se voit proposer l'examen. Sauvegardé dans la base ModKit (Plugins/ModKit/data.sqlite).
    /// </summary>
    public class ExamPattern : ModEntity<ExamPattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(ExamPattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public ExamPattern() { }

        public ExamPattern(bool isCreated)
        {
            TypeName = nameof(ExamPattern);
        }

        ModKit.ModKit Ctx { get { return Context ?? PermisArme.Instance; } }

        // ---------- Côté joueur ----------
        public void OnPlayerTrigger(Player player)
        {
            PermisArme.Examen.Proposer(player);
        }

        // ---------- Côté staff ----------
        public async Task SetProperties(int id)
        {
            ExamPattern result = await Query(id);
            Id = id;
            TypeName = nameof(ExamPattern);
            PatternName = result?.PatternName;
        }

        /// <summary>Menu principal staff : choisir un modèle et placer un point à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;

            List<ExamPattern> patterns = await QueryAll();

            Panel panel = Ctx.PanelHelper.Create("Permis d'arme - Points", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine(NovaBridge.Petit("Aucun modèle : cliquez « Nouveau modèle »"), _ => { });
            foreach (ExamPattern pattern in patterns)
            {
                panel.AddTabLine(NovaBridge.Petit("[" + pattern.Id + "] " + pattern.PatternName), async _ =>
                {
                    pattern.TypeName = nameof(ExamPattern);
                    pattern.Context = Ctx;
                    if (await Ctx.PointHelper.CreateNPoint(player, pattern))
                        NovaBridge.Notify(player, "Point « " + pattern.PatternName + " » placé à votre position.");
                    else
                        NovaBridge.Notify(player, "Erreur lors de la création du point.", false);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un nouveau modèle de point d'examen.</summary>
        public void SetPatternData(Player player)
        {
            Panel panel = Ctx.PanelHelper.Create("Nouveau point d'examen", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add(NovaBridge.Petit("Nom du point (ex. Armurerie centre)"));
            panel.SetInputPlaceholder("Examen permis d'arme");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    NovaBridge.Notify(player, "Le nom ne peut pas être vide.", false);
                    return false;
                }

                ExamPattern pattern = new ExamPattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    NovaBridge.Notify(player, "Erreur lors de l'enregistrement.", false);
                    return false;
                }

                NovaBridge.Notify(player, "Modèle « " + name + " » créé. Choisissez-le puis « Placer ici ».");
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des modèles : renommer ou supprimer (avec tous ses points).</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<ExamPattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Ctx.PanelHelper.Create("Permis d'arme - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine(NovaBridge.Petit("Aucun modèle"), _ => { });
            foreach (ExamPattern pattern in patterns)
            {
                panel.AddTabLine(NovaBridge.Petit("[" + pattern.Id + "] " + pattern.PatternName), async _ =>
                {
                    pattern.TypeName = nameof(ExamPattern);
                    pattern.Context = Ctx;

                    if (action == "rename")
                    {
                        RenamePattern(player, pattern);
                    }
                    else if (action == "delete")
                    {
                        await Ctx.PointHelper.DeleteNPointsByPattern(player, pattern);
                        if (await pattern.Delete())
                            NovaBridge.Notify(player, "Modèle « " + pattern.PatternName + " » et ses points supprimés.");
                        else
                            NovaBridge.Notify(player, "Erreur lors de la suppression.", false);
                        panel.Refresh();
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenamePattern(Player player, ExamPattern pattern)
        {
            Panel panel = Ctx.PanelHelper.Create("Renommer le point", UIPanel.PanelType.Input, player, () => RenamePattern(player, pattern));
            panel.TextLines.Add(NovaBridge.Petit("Nouveau nom pour « " + pattern.PatternName + " »"));
            panel.SetInputPlaceholder(pattern.PatternName ?? "Examen permis d'arme");

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                pattern.PatternName = name;
                return await pattern.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste de tous les points placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(ExamPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Ctx.PanelHelper.Create("Permis d'arme - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
                panel.AddTabLine(NovaBridge.Petit("Aucun point placé"), _ => { });
            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine(NovaBridge.Petit("Point #" + point.Id + " - " + name), async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Ctx.PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await Ctx.PointHelper.SetNPointPosition(player, point))
                                NovaBridge.Notify(player, "Point déplacé à votre position.");
                            break;
                        case "delete":
                            await Ctx.PointHelper.DeleteNPoint(point);
                            NovaBridge.Notify(player, "Point #" + point.Id + " supprimé.");
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
