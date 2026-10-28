using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.CheckpointSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Life.Network.Systems;
using Life.VehicleSystem;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using UnityEngine;
using Logger = ModKit.Internal.Logger;
using Random = System.Random;
using TFH = ModKit.Helper.TextFormattingHelper;

namespace AutoEcole
{
    /// <summary>
    /// Auto-école by Loris Strange.
    /// - Examen du code avec questions aléatoires (modifiables par le staff).
    /// - Examen de conduite sur un parcours et avec un véhicule configurés par le staff.
    /// - Permis à 12 points : 6 points à l'obtention, +6 après une période sans infraction.
    /// - Les radars du jeu retirent des points (nombre configurable).
    /// - L'auto-école d'origine du jeu est désactivée.
    /// Toute la configuration se fait dans AAMenu (Administration → Plugins → Auto-école) ou /autoecole.
    /// </summary>
    public class AutoEcolePlugin : ModKit.ModKit
    {
        public const string Titre = "Auto-école";

        // Raisons des débits bancaires envoyées par le jeu (Player.AddBankMoney)
        private const string RaisonRadar = "RADAR_FLASH";
        private const string RaisonAmende = "PAY_FINE";

        public static AutoEcolePlugin Instance { get; private set; }

        internal AutoEcoleConfig Config = new AutoEcoleConfig();

        private readonly Random random = new Random();
        private readonly Dictionary<int, AutoEcoleLicence> licences = new Dictionary<int, AutoEcoleLicence>();
        private readonly Dictionary<int, CodeSession> codeSessions = new Dictionary<int, CodeSession>();
        private readonly Dictionary<int, RoadExam> roadExams = new Dictionary<int, RoadExam>();
        private AutoEcolePoint pattern;
        private Vector3? originalSchoolPosition;

        public AutoEcolePlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            InitDatabase();

            // Point bleu « Auto-école » placé par le staff (AAMenu > Points bleus ou menu Auto-école)
            pattern = new AutoEcolePoint(false);
            PointHelper.AddPattern(nameof(AutoEcolePoint), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, Titre, pattern, this);
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 0, Titre, ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    if (player != null) StaffMenu(player);
                });
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /autoecole.");
            }

            DisableOriginalSchool();
            RegisterCommands();

            Nova.server.OnMinutePassedEvent += OnMinutePassed;
            Nova.server.OnPlayerBankEvent += OnPlayerBank;

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé (by Loris Strange)");
        }

        private async void InitDatabase()
        {
            try
            {
                SQLiteAsyncConnection db = ModKit.ORM.Orm.GetOrmInstance().SqliteConnection;
                await db.CreateTableAsync<AutoEcolePoint>();
                await db.CreateTableAsync<AutoEcoleConfig>();
                await db.CreateTableAsync<AutoEcoleQuestion>();
                await db.CreateTableAsync<AutoEcoleParcours>();
                await db.CreateTableAsync<AutoEcoleLicence>();

                Config = (await AutoEcoleConfig.QueryAll()).FirstOrDefault();
                if (Config == null)
                {
                    Config = new AutoEcoleConfig();
                    await Config.Save();
                }

                if ((await AutoEcoleQuestion.QueryAll()).Count == 0)
                {
                    foreach (AutoEcoleQuestion question in AutoEcoleQuestion.Defaults())
                        await question.Save();
                    Logger.LogSuccess(PluginInformations.SourceName, "questions du code par défaut ajoutées");
                }
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, "Initialisation de la base : " + e);
            }
        }

        private void RegisterCommands()
        {
            new SChatCommand("/autoecole", new[] { "/ae" }, "Configurer l'auto-école (staff)", "/autoecole",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!IsStaff(player))
                    {
                        player.Notify(Titre, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    StaffMenu(player);
                })).Register();

            new SChatCommand("/monpermis", "Voir son permis de conduire", "/monpermis",
                (Action<Player, string[]>)(async (player, args) =>
                {
                    AutoEcoleLicence licence = await GetLicence(player);
                    player.Notify("Permis de conduire", LicenceSummary(player, licence), NotificationManager.Type.Info, 10f);
                })).Register();
        }

        public override async void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);

            RemoveOriginalSchoolCheckpoint(player);
            PointHelper.InitAllNPoint(player);

            AutoEcoleLicence licence = await GetLicence(player);
            await CheckLicence(player, licence);

            // Seconde passe au cas où le jeu crée son point avec du retard
            await Task.Delay(5000);
            if (player.setup != null) RemoveOriginalSchoolCheckpoint(player);
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            foreach (RoadExam exam in roadExams.Values.ToList())
            {
                if (exam.Player?.setup == null || exam.Player.setup.connectionToClient == conn)
                    EndRoadExam(exam, false, "déconnexion");
            }
            base.OnPlayerDisconnect(conn);
        }

        public static bool IsStaff(Player player) => player != null && player.IsAdmin && player.serviceAdmin;

        private static long Now => Nova.UnixTimeNow();

        private static string C(string text, TFH.Colors color) => TFH.Color(text, color);

        // ==================================================================
        //  Désactivation de l'auto-école d'origine
        // ==================================================================

        private void DisableOriginalSchool()
        {
            try
            {
                SDrivingSchool school = Nova.server.drivingSchool;
                if (school?.config == null) return;
                originalSchoolPosition = school.config.schoolPosition.ToVector3();
                Logger.LogSuccess(PluginInformations.SourceName, "auto-école d'origine désactivée");
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, "Désactivation de l'auto-école d'origine : " + e.Message);
            }
        }

        /// <summary>
        /// Le jeu crée le point bleu de son auto-école juste avant d'appeler les plugins :
        /// on le neutralise et on le retire de la carte du joueur.
        /// </summary>
        private void RemoveOriginalSchoolCheckpoint(Player player)
        {
            if (originalSchoolPosition == null) return;
            Vector3 position = originalSchoolPosition.Value;

            List<NCheckpoint> checkpoints = Nova.server.checkpoints
                .Where(c => c != null && c.playerId == player.netId && Vector3.Distance(c.position, position) < 0.05f)
                .ToList();
            foreach (NCheckpoint checkpoint in checkpoints)
            {
                checkpoint.enterAction = _ => { };
                player.DestroyCheckpoint(checkpoint);
                Nova.server.checkpoints.Remove(checkpoint);
            }
        }

        // ==================================================================
        //  Permis à points
        // ==================================================================

        internal async Task<AutoEcoleLicence> GetLicence(Player player)
        {
            int characterId = player.character.Id;
            if (licences.TryGetValue(characterId, out AutoEcoleLicence cached))
                return cached;

            AutoEcoleLicence licence = (await AutoEcoleLicence.Query(l => l.CharacterId == characterId)).FirstOrDefault()
                                       ?? new AutoEcoleLicence { CharacterId = characterId };
            licences[characterId] = licence;
            return licence;
        }

        private async void OnMinutePassed()
        {
            foreach (Player player in Nova.server.Players.ToList())
            {
                if (player?.character == null) continue;
                if (licences.TryGetValue(player.character.Id, out AutoEcoleLicence licence))
                    await CheckLicence(player, licence);
            }
        }

        /// <summary>Suit les points du joueur : infractions, retrait du permis, fin de la période probatoire.</summary>
        internal async Task CheckLicence(Player player, AutoEcoleLicence licence)
        {
            if (!licence.PermisObtenu) return;
            Characters character = player.character;

            // Permis retiré par le jeu (plus de points) ou par la police
            if (!character.PermisB || character.PermisPoints <= 0)
            {
                await RevokeLicence(player, licence, "Votre permis de conduire a été retiré.");
                return;
            }

            bool changed = false;
            if (character.PermisPoints < licence.DerniersPoints)
            {
                licence.DebutSansInfraction = Now;
                changed = true;
                if (licence.Probatoire)
                    player.Notify("Permis probatoire", $"Vous avez perdu des points : il vous reste {character.PermisPoints} point(s). Votre période sans infraction recommence à zéro.", NotificationManager.Type.Warning, 8f);
            }
            if (character.PermisPoints != licence.DerniersPoints)
            {
                licence.DerniersPoints = character.PermisPoints;
                changed = true;
            }

            if (licence.Probatoire && Now - licence.DebutSansInfraction >= (long)Config.JoursProbatoire * 86400)
            {
                int points = Math.Min(character.PermisPoints + Config.PointsBonus, Config.PointsMax);
                character.PermisPoints = points;
                licence.DerniersPoints = points;
                licence.Probatoire = false;
                changed = true;
                await player.Save();
                player.Notify("Permis de conduire", $"Félicitations ! {Config.JoursProbatoire} jour(s) sans infraction : vous récupérez vos points. Vous avez maintenant {points} point(s).", NotificationManager.Type.Success, 10f);
            }

            if (changed) await licence.Save();
        }

        private async Task RevokeLicence(Player player, AutoEcoleLicence licence, string message)
        {
            Characters character = player.character;
            character.PermisB = false;
            character.PermisPoints = 0;
            licence.PermisObtenu = false;
            licence.Probatoire = false;
            licence.DerniersPoints = 0;
            if (Config.CodeARepasser)
            {
                licence.CodeObtenu = false;
                character.HasCode = false;
            }
            await player.Save();
            await licence.Save();

            string suite = Config.CodeARepasser ? "le code puis la conduite" : "l'examen de conduite";
            player.Notify("Permis de conduire", $"{message} Vous devez repasser {suite} à l'auto-école.", NotificationManager.Type.Error, 10f);
        }

        internal async Task GiveLicence(Player player, AutoEcoleLicence licence)
        {
            Characters character = player.character;
            character.PermisB = true;
            character.HasCode = true;
            character.PermisPoints = Config.PointsDepart;
            licence.CodeObtenu = true;
            licence.PermisObtenu = true;
            licence.Probatoire = Config.PointsDepart < Config.PointsMax;
            licence.DateObtention = Now;
            licence.DebutSansInfraction = Now;
            licence.DerniersPoints = Config.PointsDepart;
            await player.Save();
            await licence.Save();
        }

        /// <summary>
        /// Appelé par le jeu à chaque mouvement d'argent en banque. Les radars débitent avec
        /// « RADAR_FLASH » juste avant de retirer 1 point, la police avec « PAY_FINE ».
        /// </summary>
        private async void OnPlayerBank(Player player, double amount, string reason)
        {
            try
            {
                if (amount >= 0 || player?.character == null) return;
                if (reason != RaisonRadar && reason != RaisonAmende) return;

                Characters character = player.character;

                // Le jeu retire 1 point après ce débit : on retire le reste ici.
                // On ne descend jamais sous 1 pour laisser le jeu retirer le permis lui-même à 0.
                if (reason == RaisonRadar && character.PermisB && Config.PointsRadar > 1)
                    character.PermisPoints = Math.Max(1, character.PermisPoints - (Config.PointsRadar - 1));

                AutoEcoleLicence licence = await GetLicence(player);
                if (licence.PermisObtenu)
                {
                    licence.DebutSansInfraction = Now;
                    await licence.Save();
                    if (licence.Probatoire)
                        player.Notify("Permis probatoire", "Amende reçue : votre période sans infraction recommence à zéro.", NotificationManager.Type.Warning, 8f);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, "OnPlayerBank : " + e.Message);
            }
        }

        internal string LicenceSummary(Player player, AutoEcoleLicence licence)
        {
            Characters character = player.character;
            if (character.PermisB)
            {
                string text = $"Permis B : {character.PermisPoints}/{Config.PointsMax} points.";
                if (licence.PermisObtenu && licence.Probatoire)
                {
                    long remaining = (long)Config.JoursProbatoire * 86400 - (Now - licence.DebutSansInfraction);
                    text += $" Probatoire : +{Config.PointsBonus} points dans {FormatDuration(remaining)} sans infraction.";
                }
                return text;
            }
            if (character.TimestampPermis > Now)
                return $"Permis suspendu : examens impossibles avant {FormatDuration(character.TimestampPermis - Now)}.";
            return licence.CodeObtenu ? "Code obtenu : il vous reste l'examen de conduite." : "Pas de permis : commencez par l'examen du code.";
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays} j {t.Hours} h";
            if (t.TotalHours >= 1) return $"{t.Hours} h {t.Minutes} min";
            return $"{Math.Max(1, t.Minutes)} min";
        }

        // ==================================================================
        //  Menu joueur (point bleu Auto-école)
        // ==================================================================

        public async void OpenSchool(Player player)
        {
            AutoEcoleLicence licence = await GetLicence(player);
            Characters character = player.character;

            Panel panel = PanelHelper.Create(Titre, UIPanel.PanelType.TabPrice, player, () => OpenSchool(player));

            panel.AddTabLine("Mon permis", character.PermisB ? $"{character.PermisPoints}/{Config.PointsMax} pts" : "Aucun",
                IconUtils.Others.None.Id, _ => player.Notify("Permis de conduire", LicenceSummary(player, licence), NotificationManager.Type.Info, 10f));

            if (character.PermisB)
            {
                panel.AddTabLine(C("Vous avez déjà votre permis", TFH.Colors.Success), "", IconUtils.Others.None.Id, _ => { });
            }
            else if (character.TimestampPermis > Now)
            {
                panel.AddTabLine(C("Permis suspendu", TFH.Colors.Error), FormatDuration(character.TimestampPermis - Now), IconUtils.Others.None.Id,
                    _ => player.Notify(Titre, "Votre permis est suspendu, revenez plus tard.", NotificationManager.Type.Error, 5f));
            }
            else if (!licence.CodeObtenu)
            {
                panel.AddTabLine("Passer l'examen du code", $"{Config.PrixCode}€", IconUtils.Others.None.Id, _ => StartCode(player));
            }
            else
            {
                panel.AddTabLine("Passer l'examen de conduite", $"{Config.PrixConduite}€", IconUtils.Others.None.Id, _ => StartRoadExam(player));
            }

            panel.AddButton("Sélectionner", _ => panel.SelectTab());
            if (IsStaff(player))
                panel.NextButton(C("Staff", TFH.Colors.Warning), () => StaffMenu(player));
            panel.CloseButton();
            panel.Display();
        }

        private bool Pay(Player player, double price, string reason)
        {
            if (price <= 0) return true;
            if (player.Bank < price)
            {
                player.Notify(Titre, $"Il vous faut {price}€ en banque.", NotificationManager.Type.Error, 5f);
                return false;
            }
            player.AddBankMoney(-price, reason);
            return true;
        }

        // ==================================================================
        //  Examen du code
        // ==================================================================

        private async void StartCode(Player player)
        {
            if (roadExams.ContainsKey(player.character.Id)) return;

            List<AutoEcoleQuestion> all = await AutoEcoleQuestion.QueryAll();
            all = all.Where(q => q.IsValid).ToList();
            if (all.Count == 0)
            {
                player.Notify(Titre, "Aucune question n'est configurée, contactez le staff.", NotificationManager.Type.Error, 5f);
                return;
            }
            if (!Pay(player, Config.PrixCode, "AUTO_ECOLE_CODE")) return;

            CodeSession session = new CodeSession
            {
                Questions = all.OrderBy(_ => random.Next()).Take(Math.Max(1, Config.NombreQuestions)).ToList()
            };
            codeSessions[player.character.Id] = session;
            ShowQuestion(player, session);
        }

        private void ShowQuestion(Player player, CodeSession session)
        {
            if (session.Index >= session.Questions.Count)
            {
                FinishCode(player, session);
                return;
            }

            AutoEcoleQuestion question = session.Questions[session.Index];
            if (session.Answers == null)
                session.Answers = question.Answers().OrderBy(_ => random.Next()).ToList();

            Panel panel = PanelHelper.Create("Examen du code", UIPanel.PanelType.Text, player, () => ShowQuestion(player, session));
            // Texte réduit pour ne pas passer sous les boutons de réponse
            panel.TextLines.Add(TFH.Size(C($"Question {session.Index + 1}/{session.Questions.Count} : ", TFH.Colors.Info) + question.Intitule, 15));
            for (int i = 0; i < session.Answers.Count; i++)
                panel.TextLines.Add(TFH.Size($"{(char)('A' + i)}. {session.Answers[i].Text}", 13));

            for (int i = 0; i < session.Answers.Count; i++)
            {
                bool correct = session.Answers[i].Correct;
                panel.NextButton(((char)('A' + i)).ToString(), () =>
                {
                    if (correct) session.Score++;
                    session.Index++;
                    session.Answers = null;
                    ShowQuestion(player, session);
                });
            }
            panel.CloseButtonWithAction(C("Abandonner", TFH.Colors.Error), () =>
            {
                codeSessions.Remove(player.character.Id);
                player.Notify(Titre, "Examen du code abandonné.", NotificationManager.Type.Warning, 5f);
                return Task.FromResult(true);
            });
            panel.Display();
        }

        private async void FinishCode(Player player, CodeSession session)
        {
            codeSessions.Remove(player.character.Id);
            int total = session.Questions.Count;
            int required = Math.Min(Config.BonnesReponsesMin, total);
            bool success = session.Score >= required;

            Panel panel = PanelHelper.Create("Résultat du code", UIPanel.PanelType.Text, player, () => FinishCode(player, session));
            panel.TextLines.Add($"Bonnes réponses : {session.Score}/{total} (minimum {required})");

            if (success)
            {
                AutoEcoleLicence licence = await GetLicence(player);
                licence.CodeObtenu = true;
                player.character.HasCode = true;
                await licence.Save();
                await player.Save();
                panel.TextLines.Add(C("Code réussi ! Vous pouvez passer l'examen de conduite.", TFH.Colors.Success));
                panel.NextButton("Examen de conduite", () => StartRoadExam(player));
            }
            else
            {
                panel.TextLines.Add(C("Code échoué. Révisez et retentez votre chance.", TFH.Colors.Error));
            }
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Examen de conduite
        // ==================================================================

        private static Vehicle GetVehiclePrefab(int modelId)
        {
            Vehicle[] models = Nova.v?.vehicleModels;
            if (models != null && modelId >= 0 && modelId < models.Length && models[modelId] != null)
                return models[modelId];
            return Nova.server.ecoleVehicle;
        }

        private static string GetVehicleName(int modelId)
        {
            Vehicle[] models = Nova.v?.vehicleModels;
            if (models != null && modelId >= 0 && modelId < models.Length && models[modelId] != null)
                return models[modelId].VehicleName;
            return "Voiture de l'auto-école du jeu";
        }

        private async void StartRoadExam(Player player)
        {
            int characterId = player.character.Id;
            if (roadExams.ContainsKey(characterId))
            {
                player.Notify(Titre, "Vous passez déjà l'examen de conduite.", NotificationManager.Type.Error, 5f);
                return;
            }

            AutoEcoleLicence licence = await GetLicence(player);
            if (!licence.CodeObtenu)
            {
                player.Notify(Titre, "Vous devez d'abord réussir le code.", NotificationManager.Type.Error, 5f);
                return;
            }

            List<Vector3> points = (await AutoEcoleParcours.QueryAll()).OrderBy(p => p.Ordre).Select(p => p.Position).ToList();
            if (points.Count == 0)
            {
                player.Notify(Titre, "Aucun parcours n'est configuré, contactez le staff.", NotificationManager.Type.Error, 5f);
                return;
            }

            Vehicle prefab = GetVehiclePrefab(Config.ModeleVehicule);
            if (prefab == null)
            {
                player.Notify(Titre, "Aucun véhicule d'examen disponible, contactez le staff.", NotificationManager.Type.Error, 5f);
                return;
            }
            if (!Pay(player, Config.PrixConduite, "AUTO_ECOLE_CONDUITE")) return;

            Vector3 position;
            Quaternion rotation;
            if (Config.SpawnDefini)
            {
                position = new Vector3(Config.SpawnX, Config.SpawnY, Config.SpawnZ);
                rotation = Quaternion.Euler(0f, Config.SpawnRotation, 0f);
            }
            else
            {
                Transform t = player.setup.transform;
                position = t.position + t.right * 4f + Vector3.up * 0.5f;
                rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
            }

            Vehicle vehicle = UnityEngine.Object.Instantiate(prefab, position, rotation);
            vehicle.Networkplate = string.IsNullOrWhiteSpace(Config.Plaque) ? "AUTO-ECOLE" : Config.Plaque;
            vehicle.quickOwner = characterId;
            NetworkServer.Spawn(vehicle.gameObject);

            RoadExam exam = new RoadExam
            {
                Player = player,
                CharacterId = characterId,
                Vehicle = vehicle,
                Points = points,
                EndTime = Now + Math.Max(1, Config.TempsConduiteMinutes) * 60L
            };
            roadExams[characterId] = exam;

            player.setup.TargetSetGPSTarget(position);
            player.Notify(Titre,
                $"Montez dans le véhicule « {vehicle.Networkplate} » et suivez les points orange. " +
                $"Temps : {Config.TempsConduiteMinutes} min" + (Config.VitesseMax > 0 ? $", vitesse max {Config.VitesseMax} km/h." : "."),
                NotificationManager.Type.Info, 10f);

            NextRoadCheckpoint(exam);
            RunRoadExam(exam);
        }

        private void NextRoadCheckpoint(RoadExam exam)
        {
            if (exam.Finished) return;
            ClearRoadCheckpoint(exam);

            if (exam.Index >= exam.Points.Count)
            {
                EndRoadExam(exam, true, null);
                return;
            }

            Player player = exam.Player;
            Vector3 target = exam.Points[exam.Index];
            NVehicleCheckpoint checkpoint = new NVehicleCheckpoint(player.netId, target, (Action<NVehicleCheckpoint, uint>)((cp, vehicleId) =>
            {
                if (exam.Finished || cp != exam.Checkpoint) return;
                if (exam.Vehicle == null || (vehicleId != exam.Vehicle.netId && player.GetVehicleId() != exam.Vehicle.netId))
                {
                    player.Notify(Titre, "Vous devez utiliser le véhicule de l'auto-école.", NotificationManager.Type.Warning, 4f);
                    return;
                }
                exam.Index++;
                if (exam.Index < exam.Points.Count)
                    player.Notify(Titre, $"Point de passage {exam.Index}/{exam.Points.Count} validé.", NotificationManager.Type.Info, 3f);
                NextRoadCheckpoint(exam);
            }));
            exam.Checkpoint = checkpoint;
            player.CreateVehicleCheckpoint(checkpoint);
            if (exam.Started) player.setup.TargetSetGPSTarget(target);
        }

        private static void ClearRoadCheckpoint(RoadExam exam)
        {
            if (exam.Checkpoint == null) return;
            try { exam.Player?.DestroyVehicleCheckpoint(exam.Checkpoint); } catch { /* joueur déconnecté */ }
            Nova.server.vehicleCheckpoints?.Remove(exam.Checkpoint);
            exam.Checkpoint = null;
        }

        /// <summary>Surveillance chaque seconde : temps, sortie du véhicule, excès de vitesse.</summary>
        private async void RunRoadExam(RoadExam exam)
        {
            Vector3? lastPosition = null;
            DateTime lastTime = DateTime.UtcNow;

            while (!exam.Finished)
            {
                await Task.Delay(1000);
                if (exam.Finished) return;

                try
                {
                    Player player = exam.Player;
                    if (player?.setup == null || !Nova.server.Players.Contains(player))
                    {
                        EndRoadExam(exam, false, "déconnexion");
                        return;
                    }
                    if (exam.Vehicle == null)
                    {
                        EndRoadExam(exam, false, "le véhicule a été détruit");
                        return;
                    }
                    if (Now > exam.EndTime)
                    {
                        EndRoadExam(exam, false, "temps écoulé");
                        return;
                    }

                    bool inVehicle = player.GetVehicleId() == exam.Vehicle.netId;
                    if (inVehicle)
                    {
                        if (!exam.Started)
                        {
                            exam.Started = true;
                            player.setup.TargetSetGPSTarget(exam.Points[exam.Index]);
                            player.Notify(Titre, "C'est parti ! Suivez les points de passage.", NotificationManager.Type.Success, 4f);
                        }
                        exam.SecondsOutside = 0;
                    }
                    else if (exam.Started)
                    {
                        exam.SecondsOutside++;
                        if (exam.SecondsOutside == 1)
                            player.Notify(Titre, "Remontez dans le véhicule dans les 30 secondes ou l'examen sera échoué.", NotificationManager.Type.Warning, 5f);
                        if (exam.SecondsOutside > 30)
                        {
                            EndRoadExam(exam, false, "vous avez quitté le véhicule");
                            return;
                        }
                    }

                    // Vitesse calculée à partir du déplacement réel du véhicule
                    Vector3 position = exam.Vehicle.transform.position;
                    DateTime time = DateTime.UtcNow;
                    if (inVehicle && lastPosition != null && Config.VitesseMax > 0)
                    {
                        double seconds = Math.Max(0.1, (time - lastTime).TotalSeconds);
                        int speed = (int)(Vector3.Distance(position, lastPosition.Value) / seconds * 3.6);
                        if (speed > Config.VitesseMax)
                        {
                            if (!exam.Speeding)
                            {
                                exam.Speeding = true;
                                exam.SpeedFaults++;
                                if (exam.SpeedFaults > Config.FautesVitesseMax)
                                {
                                    EndRoadExam(exam, false, $"trop d'excès de vitesse ({speed} km/h)");
                                    return;
                                }
                                player.Notify(Titre, $"Excès de vitesse : {speed} km/h (max {Config.VitesseMax}). Faute {exam.SpeedFaults}/{Config.FautesVitesseMax}.", NotificationManager.Type.Warning, 5f);
                            }
                        }
                        else if (speed < Config.VitesseMax - 5)
                        {
                            exam.Speeding = false;
                        }
                    }
                    lastPosition = position;
                    lastTime = time;
                }
                catch (Exception e)
                {
                    Logger.LogError(PluginInformations.SourceName, "Examen de conduite : " + e.Message);
                }
            }
        }

        private async void EndRoadExam(RoadExam exam, bool success, string reason)
        {
            if (exam.Finished) return;
            exam.Finished = true;
            roadExams.Remove(exam.CharacterId);
            ClearRoadCheckpoint(exam);

            if (exam.Vehicle != null)
            {
                try { NetworkServer.Destroy(exam.Vehicle.gameObject); }
                catch (Exception e) { Logger.LogError(PluginInformations.SourceName, "Suppression du véhicule : " + e.Message); }
            }

            Player player = exam.Player;
            if (player?.character == null || !Nova.server.Players.Contains(player)) return;

            if (success)
            {
                AutoEcoleLicence licence = await GetLicence(player);
                await GiveLicence(player, licence);
                player.Notify(Titre,
                    $"Félicitations, vous avez votre permis ! Vous démarrez avec {Config.PointsDepart} point(s). " +
                    (licence.Probatoire ? $"Après {Config.JoursProbatoire} jour(s) sans infraction ni amende, vous recevrez {Config.PointsBonus} point(s) de plus." : ""),
                    NotificationManager.Type.Success, 12f);
            }
            else
            {
                player.Notify(Titre, $"Examen de conduite échoué : {reason}.", NotificationManager.Type.Error, 8f);
            }
        }

        // ==================================================================
        //  Menu staff
        // ==================================================================

        public async void StaffMenu(Player player)
        {
            if (!player.IsAdmin) return;

            int questions = (await AutoEcoleQuestion.QueryAll()).Count;
            int points = (await AutoEcoleParcours.QueryAll()).Count;

            Panel panel = PanelHelper.Create($"{Titre} - Staff", UIPanel.PanelType.Tab, player, () => StaffMenu(player));
            panel.AddTabLine($"Questions du code ({questions})", _ => QuestionsMenu(player));
            panel.AddTabLine($"Parcours de conduite ({points} point(s))", _ => RouteMenu(player));
            panel.AddTabLine($"Véhicule d'examen ({GetVehicleName(Config.ModeleVehicule)})", _ => VehicleMenu(player));
            panel.AddTabLine("Réglages (prix, points, examens)", _ => SettingsMenu(player));
            panel.AddTabLine("Emplacements de l'auto-école (points bleus)", _ =>
            {
                pattern.Context = this;
                pattern.CreateOrGenerate(player);
            });
            panel.AddTabLine("Permis des joueurs connectés", _ => PlayersMenu(player));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Questions ----------------------------

        private async void QuestionsMenu(Player player)
        {
            List<AutoEcoleQuestion> questions = await AutoEcoleQuestion.QueryAll();

            Panel panel = PanelHelper.Create($"{Titre} - Questions", UIPanel.PanelType.Tab, player, () => QuestionsMenu(player));
            if (questions.Count == 0)
                panel.AddTabLine("Aucune question", _ => { });
            foreach (AutoEcoleQuestion question in questions)
            {
                string label = question.Intitule.Length > 60 ? question.Intitule.Substring(0, 57) + "..." : question.Intitule;
                panel.AddTabLine($"[{question.Id}] {label}", _ => EditQuestion(player, question));
            }

            if (questions.Count > 0)
                panel.NextButton("Modifier", () => panel.SelectTab());
            panel.NextButton("Ajouter", () => AddQuestionStep(player, new AutoEcoleQuestion(), 0));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static readonly string[] QuestionSteps =
        {
            "Intitulé de la question",
            "Bonne réponse",
            "Mauvaise réponse n°1",
            "Mauvaise réponse n°2 (facultatif : laissez vide)",
            "Mauvaise réponse n°3 (facultatif : laissez vide)"
        };

        /// <summary>Assistant de création d'une question, une étape par champ.</summary>
        private void AddQuestionStep(Player player, AutoEcoleQuestion question, int step)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Nouvelle question", UIPanel.PanelType.Input, player, () => AddQuestionStep(player, question, step));
            panel.TextLines.Add($"Étape {step + 1}/{QuestionSteps.Length} : {QuestionSteps[step]}");
            if (step > 0) panel.TextLines.Add(C(question.Intitule, TFH.Colors.Grey));
            panel.SetInputPlaceholder(QuestionSteps[step]);

            panel.AddButton(step < QuestionSteps.Length - 1 ? "Suivant" : "Enregistrer", async _ =>
            {
                string value = panel.inputText?.Trim() ?? "";
                if (value.Length == 0 && step < 3)
                {
                    player.Notify(Titre, "Ce champ est obligatoire.", NotificationManager.Type.Error, 4f);
                    return;
                }
                question.SetField(step, value);

                if (step < QuestionSteps.Length - 1)
                {
                    player.ClosePanel(panel);
                    AddQuestionStep(player, question, step + 1);
                    return;
                }
                if (await question.Save())
                    player.Notify(Titre, "Question ajoutée.", NotificationManager.Type.Success, 4f);
                else
                    player.Notify(Titre, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 4f);
                panel.Previous();
            });
            panel.PreviousButton("Annuler");
            panel.CloseButton();
            panel.Display();
        }

        private void EditQuestion(Player player, AutoEcoleQuestion question)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Question #{question.Id}", UIPanel.PanelType.Tab, player, () => EditQuestion(player, question));
            for (int i = 0; i < QuestionSteps.Length; i++)
            {
                int field = i;
                string value = question.GetField(field);
                string prefix = field == 0 ? "Question" : field == 1 ? C("Bonne réponse", TFH.Colors.Success) : C($"Mauvaise réponse {field - 1}", TFH.Colors.Error);
                panel.AddTabLine($"{prefix} : {(string.IsNullOrEmpty(value) ? "(vide)" : value)}", _ => EditQuestionField(player, question, field));
            }

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.AddButton(C("Supprimer", TFH.Colors.Error), async _ =>
            {
                if (await question.Delete())
                    player.Notify(Titre, "Question supprimée.", NotificationManager.Type.Success, 4f);
                panel.Previous();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditQuestionField(Player player, AutoEcoleQuestion question, int field)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Modifier", UIPanel.PanelType.Input, player, () => EditQuestionField(player, question, field));
            panel.TextLines.Add(QuestionSteps[field]);
            panel.TextLines.Add(C("Actuel : " + question.GetField(field), TFH.Colors.Grey));
            panel.SetInputPlaceholder(question.GetField(field));

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string value = panel.inputText?.Trim() ?? "";
                if (value.Length == 0 && field < 3)
                {
                    player.Notify(Titre, "Ce champ est obligatoire.", NotificationManager.Type.Error, 4f);
                    return false;
                }
                question.SetField(field, value);
                return await question.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ----------------------------- Parcours ----------------------------

        private async void RouteMenu(Player player)
        {
            List<AutoEcoleParcours> points = (await AutoEcoleParcours.QueryAll()).OrderBy(p => p.Ordre).ToList();
            string action = "";

            Panel panel = PanelHelper.Create($"{Titre} - Parcours", UIPanel.PanelType.Tab, player, () => RouteMenu(player));
            if (points.Count == 0)
                panel.AddTabLine("Aucun point : placez-vous sur la route puis « Ajouter ici »", _ => { });

            for (int i = 0; i < points.Count; i++)
            {
                AutoEcoleParcours point = points[i];
                int index = i;
                panel.AddTabLine($"Point n°{i + 1}  ({point.X:0}, {point.Y:0}, {point.Z:0})", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            player.setup.TargetSetPosition(point.Position);
                            break;
                        case "move":
                            point.Position = player.setup.transform.position;
                            await point.Save();
                            player.Notify(Titre, $"Point n°{index + 1} déplacé à votre position.", NotificationManager.Type.Success, 4f);
                            panel.Refresh();
                            break;
                        case "up":
                            if (index > 0)
                            {
                                AutoEcoleParcours previous = points[index - 1];
                                (previous.Ordre, point.Ordre) = (point.Ordre, previous.Ordre);
                                await previous.Save();
                                await point.Save();
                            }
                            panel.Refresh();
                            break;
                        case "delete":
                            await point.Delete();
                            player.Notify(Titre, $"Point n°{index + 1} supprimé.", NotificationManager.Type.Success, 4f);
                            panel.Refresh();
                            break;
                    }
                });
            }

            panel.AddButton(C("Ajouter ici", TFH.Colors.Success), async _ =>
            {
                AutoEcoleParcours point = new AutoEcoleParcours
                {
                    Ordre = points.Count == 0 ? 1 : points.Max(p => p.Ordre) + 1,
                    Position = player.setup.transform.position
                };
                if (await point.Save())
                    player.Notify(Titre, $"Point n°{points.Count + 1} ajouté à votre position.", NotificationManager.Type.Success, 4f);
                panel.Refresh();
            });
            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton("Monter", _ => { action = "up"; panel.SelectTab(); });
                panel.AddButton(C("Supprimer", TFH.Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ----------------------------- Véhicule ----------------------------

        private void VehicleMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Véhicule", UIPanel.PanelType.Tab, player, () => VehicleMenu(player));
            panel.AddTabLine($"Modèle : {GetVehicleName(Config.ModeleVehicule)}", _ => VehicleModelMenu(player));
            panel.AddTabLine($"Plaque : {Config.Plaque}", _ => EditSetting(player, "Plaque du véhicule", Config.Plaque, value =>
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 12) return false;
                Config.Plaque = value.ToUpperInvariant();
                return true;
            }));
            panel.AddTabLine(Config.SpawnDefini
                    ? $"Apparition : ({Config.SpawnX:0}, {Config.SpawnY:0}, {Config.SpawnZ:0}) → définir ici"
                    : "Apparition : à côté du joueur → définir ici", async _ =>
            {
                Transform t = player.setup.transform;
                Config.SpawnX = t.position.x;
                Config.SpawnY = t.position.y + 0.5f;
                Config.SpawnZ = t.position.z;
                Config.SpawnRotation = t.eulerAngles.y;
                Config.SpawnDefini = true;
                await Config.Save();
                player.Notify(Titre, "Le véhicule d'examen apparaîtra ici, orienté comme vous.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            if (Config.SpawnDefini)
                panel.AddTabLine("Se téléporter au point d'apparition", _ => player.setup.TargetSetPosition(new Vector3(Config.SpawnX, Config.SpawnY, Config.SpawnZ)));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void VehicleModelMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Modèle", UIPanel.PanelType.TabPrice, player, () => VehicleModelMenu(player));

            panel.AddTabLine("Voiture de l'auto-école du jeu", Config.ModeleVehicule < 0 ? "✔" : "", IconUtils.Others.None.Id, async _ =>
            {
                Config.ModeleVehicule = -1;
                await Config.Save();
                panel.Previous();
            });

            Vehicle[] models = Nova.v?.vehicleModels ?? new Vehicle[0];
            for (int i = 0; i < models.Length; i++)
            {
                if (models[i] == null) continue;
                int modelId = i;
                int icon;
                try { icon = VehicleUtils.GetIconId(modelId); } catch { icon = IconUtils.Others.None.Id; }
                panel.AddTabLine($"[{modelId}] {models[i].VehicleName}", Config.ModeleVehicule == modelId ? "✔" : "", icon, async _ =>
                {
                    Config.ModeleVehicule = modelId;
                    await Config.Save();
                    player.Notify(Titre, $"Véhicule d'examen : {models[modelId].VehicleName}.", NotificationManager.Type.Success, 4f);
                    panel.Previous();
                });
            }

            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ----------------------------- Réglages ----------------------------

        private void SettingsMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Réglages", UIPanel.PanelType.Tab, player, () => SettingsMenu(player));

            AddIntSetting(panel, player, "Questions par examen de code", () => Config.NombreQuestions, v => Config.NombreQuestions = v, 1, 100);
            AddIntSetting(panel, player, "Bonnes réponses minimum", () => Config.BonnesReponsesMin, v => Config.BonnesReponsesMin = v, 1, 100);
            AddDoubleSetting(panel, player, "Prix du code (€)", () => Config.PrixCode, v => Config.PrixCode = v);
            AddDoubleSetting(panel, player, "Prix de la conduite (€)", () => Config.PrixConduite, v => Config.PrixConduite = v);
            AddIntSetting(panel, player, "Points à l'obtention du permis", () => Config.PointsDepart, v => Config.PointsDepart = v, 1, 100);
            AddIntSetting(panel, player, "Points maximum", () => Config.PointsMax, v => Config.PointsMax = v, 1, 100);
            AddIntSetting(panel, player, "Points rendus après la période probatoire", () => Config.PointsBonus, v => Config.PointsBonus = v, 0, 100);
            AddIntSetting(panel, player, "Jours sans infraction pour récupérer les points", () => Config.JoursProbatoire, v => Config.JoursProbatoire = v, 0, 365);
            AddIntSetting(panel, player, "Points retirés par un radar", () => Config.PointsRadar, v => Config.PointsRadar = v, 1, 12);
            AddIntSetting(panel, player, "Durée de l'examen de conduite (min)", () => Config.TempsConduiteMinutes, v => Config.TempsConduiteMinutes = v, 1, 120);
            AddIntSetting(panel, player, "Vitesse max à l'examen (km/h, 0 = aucune)", () => Config.VitesseMax, v => Config.VitesseMax = v, 0, 400);
            AddIntSetting(panel, player, "Excès de vitesse tolérés à l'examen", () => Config.FautesVitesseMax, v => Config.FautesVitesseMax = v, 0, 50);
            panel.AddTabLine($"Permis retiré → repasser le code : {(Config.CodeARepasser ? "oui" : "non")}", async _ =>
            {
                Config.CodeARepasser = !Config.CodeARepasser;
                await Config.Save();
                panel.Refresh();
            });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AddIntSetting(Panel panel, Player player, string label, Func<int> get, Action<int> set, int min, int max)
        {
            panel.AddTabLine($"{label} : {get()}", _ => EditSetting(player, label, get().ToString(), value =>
            {
                if (!int.TryParse(value, out int v) || v < min || v > max) return false;
                set(v);
                return true;
            }));
        }

        private void AddDoubleSetting(Panel panel, Player player, string label, Func<double> get, Action<double> set)
        {
            panel.AddTabLine($"{label} : {get()}", _ => EditSetting(player, label, get().ToString(CultureInfo.InvariantCulture), value =>
            {
                if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0) return false;
                set(v);
                return true;
            }));
        }

        private void EditSetting(Player player, string label, string current, Func<string, bool> apply)
        {
            Panel panel = PanelHelper.Create($"{Titre} - {label}", UIPanel.PanelType.Input, player, () => EditSetting(player, label, current, apply));
            panel.TextLines.Add(label);
            panel.TextLines.Add(C("Valeur actuelle : " + current, TFH.Colors.Grey));
            panel.SetInputPlaceholder(current);

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!apply(panel.inputText?.Trim() ?? ""))
                {
                    player.Notify(Titre, "Valeur invalide.", NotificationManager.Type.Error, 4f);
                    return false;
                }
                return await Config.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------- Permis des joueurs ----------------------

        private void PlayersMenu(Player player)
        {
            Panel panel = PanelHelper.Create($"{Titre} - Joueurs", UIPanel.PanelType.Tab, player, () => PlayersMenu(player));
            foreach (Player target in Nova.server.Players.Where(p => p?.character != null))
            {
                string state = target.character.PermisB ? $"{target.character.PermisPoints} pts" : "sans permis";
                panel.AddTabLine($"{target.FullName} ({state})", _ => PlayerLicenceMenu(player, target));
            }
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void PlayerLicenceMenu(Player player, Player target)
        {
            AutoEcoleLicence licence = await GetLicence(target);

            Panel panel = PanelHelper.Create($"{Titre} - {target.FullName}", UIPanel.PanelType.Tab, player, () => PlayerLicenceMenu(player, target));
            panel.AddTabLine(LicenceSummary(target, licence), _ => { });
            panel.AddTabLine("Valider le code", async _ =>
            {
                licence.CodeObtenu = true;
                target.character.HasCode = true;
                await licence.Save();
                await target.Save();
                player.Notify(Titre, $"Code validé pour {target.FullName}.", NotificationManager.Type.Success, 4f);
                panel.Refresh();
            });
            panel.AddTabLine($"Donner le permis ({Config.PointsDepart} points, probatoire)", async _ =>
            {
                await GiveLicence(target, licence);
                target.Notify(Titre, "Le staff vous a attribué le permis de conduire.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddTabLine("Modifier les points", ui => EditSetting(player, "Points du permis", target.character.PermisPoints.ToString(), value =>
            {
                if (!int.TryParse(value, out int v) || v < 1 || v > Config.PointsMax || !target.character.PermisB) return false;
                target.character.PermisPoints = v;
                licence.DerniersPoints = v;
                _ = licence.Save();
                _ = target.Save();
                return true;
            }));
            panel.AddTabLine("Terminer la période probatoire", async _ =>
            {
                if (!licence.PermisObtenu || !licence.Probatoire) return;
                licence.DebutSansInfraction = 0;
                await CheckLicence(target, licence);
                panel.Refresh();
            });
            panel.AddTabLine(C("Retirer le permis", TFH.Colors.Error), async _ =>
            {
                await RevokeLicence(target, licence, "Le staff vous a retiré le permis de conduire.");
                panel.Refresh();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }

    // ======================================================================
    //  Sessions d'examen (en mémoire)
    // ======================================================================

    internal class CodeSession
    {
        public List<AutoEcoleQuestion> Questions;
        public List<(string Text, bool Correct)> Answers;
        public int Index;
        public int Score;
    }

    internal class RoadExam
    {
        public Player Player;
        public int CharacterId;
        public Vehicle Vehicle;
        public List<Vector3> Points;
        public int Index;
        public NVehicleCheckpoint Checkpoint;
        public long EndTime;
        public bool Started;
        public bool Finished;
        public int SecondsOutside;
        public bool Speeding;
        public int SpeedFaults;
    }

    // ======================================================================
    //  Tables (base ModKit : Plugins/ModKit/data.sqlite)
    // ======================================================================

    public class AutoEcoleConfig : ModEntity<AutoEcoleConfig>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int NombreQuestions { get; set; } = 10;
        public int BonnesReponsesMin { get; set; } = 8;
        public double PrixCode { get; set; } = 150;
        public double PrixConduite { get; set; } = 300;
        public int PointsDepart { get; set; } = 6;
        public int PointsMax { get; set; } = 12;
        public int PointsBonus { get; set; } = 6;
        public int JoursProbatoire { get; set; } = 7;
        public int PointsRadar { get; set; } = 1;
        public bool CodeARepasser { get; set; } = true;
        public int TempsConduiteMinutes { get; set; } = 15;
        public int VitesseMax { get; set; } = 90;
        public int FautesVitesseMax { get; set; } = 2;
        /// <summary>Index dans Nova.v.vehicleModels, -1 = voiture de l'auto-école du jeu.</summary>
        public int ModeleVehicule { get; set; } = -1;
        public string Plaque { get; set; } = "AUTO-ECOLE";
        public bool SpawnDefini { get; set; }
        public float SpawnX { get; set; }
        public float SpawnY { get; set; }
        public float SpawnZ { get; set; }
        public float SpawnRotation { get; set; }
    }

    public class AutoEcoleLicence : ModEntity<AutoEcoleLicence>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        [Indexed] public int CharacterId { get; set; }
        public bool CodeObtenu { get; set; }
        public bool PermisObtenu { get; set; }
        public bool Probatoire { get; set; }
        public long DateObtention { get; set; }
        /// <summary>Début de la période en cours sans amende ni perte de points.</summary>
        public long DebutSansInfraction { get; set; }
        public int DerniersPoints { get; set; }
    }

    public class AutoEcoleParcours : ModEntity<AutoEcoleParcours>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int Ordre { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        [Ignore]
        public Vector3 Position
        {
            get => new Vector3(X, Y, Z);
            set { X = value.x; Y = value.y; Z = value.z; }
        }
    }

    public class AutoEcoleQuestion : ModEntity<AutoEcoleQuestion>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public string Intitule { get; set; } = "";
        public string BonneReponse { get; set; } = "";
        public string MauvaiseReponse1 { get; set; } = "";
        public string MauvaiseReponse2 { get; set; } = "";
        public string MauvaiseReponse3 { get; set; } = "";

        [Ignore]
        public bool IsValid => !string.IsNullOrWhiteSpace(Intitule) && !string.IsNullOrWhiteSpace(BonneReponse) && !string.IsNullOrWhiteSpace(MauvaiseReponse1);

        public List<(string Text, bool Correct)> Answers()
        {
            List<(string, bool)> answers = new List<(string, bool)> { (BonneReponse, true) };
            foreach (string wrong in new[] { MauvaiseReponse1, MauvaiseReponse2, MauvaiseReponse3 })
                if (!string.IsNullOrWhiteSpace(wrong)) answers.Add((wrong, false));
            return answers;
        }

        public string GetField(int field)
        {
            switch (field)
            {
                case 0: return Intitule;
                case 1: return BonneReponse;
                case 2: return MauvaiseReponse1;
                case 3: return MauvaiseReponse2;
                default: return MauvaiseReponse3;
            }
        }

        public void SetField(int field, string value)
        {
            switch (field)
            {
                case 0: Intitule = value; break;
                case 1: BonneReponse = value; break;
                case 2: MauvaiseReponse1 = value; break;
                case 3: MauvaiseReponse2 = value; break;
                default: MauvaiseReponse3 = value; break;
            }
        }

        private static AutoEcoleQuestion Q(string intitule, string bonne, string m1, string m2 = "", string m3 = "") =>
            new AutoEcoleQuestion { Intitule = intitule, BonneReponse = bonne, MauvaiseReponse1 = m1, MauvaiseReponse2 = m2, MauvaiseReponse3 = m3 };

        public static IEnumerable<AutoEcoleQuestion> Defaults() => new[]
        {
            Q("Quelle est la vitesse maximale en agglomération ?", "50 km/h", "30 km/h", "70 km/h", "90 km/h"),
            Q("Quelle est la vitesse maximale sur route hors agglomération ?", "80 km/h", "90 km/h", "110 km/h", "70 km/h"),
            Q("Quelle est la vitesse maximale sur autoroute par temps sec ?", "130 km/h", "110 km/h", "150 km/h", "120 km/h"),
            Q("À un feu orange fixe, je dois :", "M'arrêter, sauf si cela est dangereux", "Accélérer pour passer", "Klaxonner", "Passer sans ralentir"),
            Q("Un panneau STOP m'oblige à :", "Marquer un arrêt complet et céder le passage", "Ralentir seulement", "Céder le passage sans m'arrêter", "Klaxonner avant de passer"),
            Q("Sauf indication contraire, à une intersection la priorité est :", "À droite", "À gauche", "Au plus rapide", "Au plus gros véhicule"),
            Q("Le port de la ceinture de sécurité est obligatoire :", "Pour tous les occupants du véhicule", "Seulement pour le conducteur", "Seulement sur autoroute", "Seulement la nuit"),
            Q("Puis-je téléphoner en tenant mon téléphone en conduisant ?", "Non, c'est interdit", "Oui, en agglomération", "Oui, si je roule lentement", "Oui, sur autoroute"),
            Q("Quel est le taux d'alcool maximal autorisé pour un jeune conducteur ?", "0,2 g/L de sang", "0,5 g/L de sang", "0,8 g/L de sang", "1 g/L de sang"),
            Q("Un véhicule de secours arrive avec sirène et gyrophare :", "Je facilite son passage", "Je l'ignore", "J'accélère devant lui", "Je m'arrête au milieu de la route"),
            Q("Combien de points compte le permis probatoire au départ ?", "6 points", "12 points", "3 points", "8 points"),
            Q("Un piéton est engagé sur un passage piéton :", "Je m'arrête pour le laisser passer", "Je klaxonne", "Je le contourne", "Je garde ma vitesse"),
            Q("Par temps de pluie, la distance de freinage :", "Augmente", "Diminue", "Ne change pas", "Est divisée par deux"),
            Q("Pour dépasser un véhicule, je dois :", "Contrôler mes rétroviseurs et mettre mon clignotant", "Dépasser par la droite", "Dépasser dans un virage", "Klaxonner longuement"),
            Q("La ligne blanche continue au milieu de la route :", "Ne doit pas être franchie", "Peut être franchie pour dépasser", "Indique un parking", "N'a aucune signification"),
            Q("Je suis flashé par un radar avec le permis :", "Je reçois une amende et je perds des points", "Rien ne se passe", "Je gagne des points", "Seul le passager est sanctionné")
        };
    }

    // ======================================================================
    //  Point bleu « Auto-école » (placé par le staff)
    // ======================================================================

    public class AutoEcolePoint : ModEntity<AutoEcolePoint>, PatternData
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public string PatternName { get; set; }
        [Ignore] public string TypeName { get; set; } = nameof(AutoEcolePoint);
        [Ignore] public ModKit.ModKit Context { get; set; }

        public AutoEcolePoint() { }

        public AutoEcolePoint(bool isCreated)
        {
            TypeName = nameof(AutoEcolePoint);
        }

        public void OnPlayerTrigger(Player player)
        {
            AutoEcolePlugin.Instance?.OpenSchool(player);
        }

        public async Task SetProperties(int id)
        {
            AutoEcolePoint result = await Query(id);
            Id = id;
            TypeName = nameof(AutoEcolePoint);
            PatternName = result?.PatternName;
        }

        /// <summary>Menu staff : choisir une auto-école et la placer à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;
            List<AutoEcolePoint> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Auto-école - Placer un point", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucune auto-école, créez-en une avec « Nouveau »", _ => { });
            foreach (AutoEcolePoint pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(AutoEcolePoint);
                    pattern.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        player.Notify("Auto-école", $"« {pattern.PatternName} » placée à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify("Auto-école", "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau", () => SetPatternData(player));
            panel.NextButton("Auto-écoles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Auto-école - Nouvelle", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom de l'auto-école (affiché aux joueurs)");
            panel.SetInputPlaceholder("Auto-école");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                AutoEcolePoint pattern = new AutoEcolePoint(false) { PatternName = name };
                if (!await pattern.Save()) return false;
                player.Notify("Auto-école", $"« {name} » créée. Choisissez-la puis « Placer ici ».", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<AutoEcolePoint> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Auto-école - Liste", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucune auto-école", _ => { });
            foreach (AutoEcolePoint pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    if (!forEdit) return;
                    pattern.TypeName = nameof(AutoEcolePoint);
                    pattern.Context = Context;
                    await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                    if (await pattern.Delete())
                        player.Notify("Auto-école", $"« {pattern.PatternName} » et ses points supprimés.", NotificationManager.Type.Success, 5f);
                    panel.Refresh();
                });
            }
            if (patterns.Count > 0 && forEdit)
                panel.AddButton(TFH.Color("Supprimer", TFH.Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(AutoEcolePoint));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("Auto-école - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));
            if (points.Count == 0)
                panel.AddTabLine("Aucun point placé", _ => { });
            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Context.PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await Context.PointHelper.SetNPointPosition(player, point))
                                player.Notify("Auto-école", "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            player.Notify("Auto-école", $"Point #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }
            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(TFH.Color("Supprimer", TFH.Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
