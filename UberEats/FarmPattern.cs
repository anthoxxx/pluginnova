using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace UberEats
{
    /// <summary>
    /// Point de farm : le staff place des points bleus où les joueurs récoltent un objet
    /// (ingrédients pour les restaurants…). Tout est réglable par modèle : objet, quantité,
    /// durée de récolte, délai entre deux récoltes et accès réservé au métier restaurateur.
    /// Placement : AAMenu > Administration > Points bleus > Farm Uber Eats (ou Administration > Plugins > Uber Eats).
    /// </summary>
    public class FarmPattern : ModEntity<FarmPattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }
        public int ItemId { get; set; }
        public int MinQuantity { get; set; } = 1;
        public int MaxQuantity { get; set; } = 3;
        /// <summary>Temps (s) à rester sur place pour récolter.</summary>
        public int DurationSeconds { get; set; } = 10;
        /// <summary>Délai (s) avant qu'un même joueur puisse récolter à nouveau ce modèle.</summary>
        public int CooldownSeconds { get; set; } = 60;
        public bool RestaurateurOnly { get; set; } = true;

        [Ignore]
        public string TypeName { get; set; } = nameof(FarmPattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        [Ignore]
        private UberEatsPlugin Plugin => (UberEatsPlugin)Context;

        /// <summary>Dernière récolte par (personnage, modèle).</summary>
        private static readonly Dictionary<(int characterId, int patternId), long> LastHarvest = new Dictionary<(int, int), long>();

        /// <summary>Joueurs en train de récolter (une seule récolte à la fois).</summary>
        private static readonly HashSet<int> Harvesting = new HashSet<int>();

        /// <summary>Distance max (m) dont le joueur peut bouger pendant la récolte.</summary>
        private const float MaxMoveDistance = 3f;

        public FarmPattern() { }

        public FarmPattern(bool isCreated)
        {
            TypeName = nameof(FarmPattern);
        }

        // ------------------------------------------------------------------
        //  Côté joueur : récolte
        // ------------------------------------------------------------------

        public async void OnPlayerTrigger(Player player)
        {
            // Le modèle est partagé par tous les points : on relit ses valeurs pour ce joueur.
            FarmPattern farm = await Query(Id);
            if (farm == null) return;
            ShowFarm(player, farm);
        }

        private void ShowFarm(Player player, FarmPattern farm)
        {
            if (farm.ItemId == 0)
            {
                player.Notify(farm.PatternName ?? "Farm", "Ce point n'est pas encore configuré par le staff.", NotificationManager.Type.Warning, 5f);
                return;
            }
            if (farm.RestaurateurOnly && !Plugin.IsRestaurateur(player))
            {
                player.Notify(farm.PatternName, "Réservé aux employés d'un restaurant partenaire.", NotificationManager.Type.Warning, 5f);
                return;
            }

            string itemName = UberEatsPlugin.ItemName(player, farm.ItemId);
            long remaining = RemainingCooldown(player, farm);

            Panel panel = Context.PanelHelper.Create(farm.PatternName, UIPanel.PanelType.Tab, player, () => ShowFarm(player, farm));
            panel.AddInfo($"Récolte : {Bold(itemName)} x{farm.MinQuantity}" + (farm.MaxQuantity > farm.MinQuantity ? $" à {farm.MaxQuantity}" : ""));
            panel.AddInfo($"Durée : {farm.DurationSeconds} s");
            panel.AddInfo(Color("Ne bougez pas pendant la récolte", Colors.Grey));
            if (remaining > 0)
                panel.AddInfo(Color($"Disponible dans {FormatDuration(remaining)}", Colors.Warning));

            if (remaining <= 0)
                panel.CloseButtonWithAction(Color("Récolter", Colors.Success), async () =>
                {
                    await Harvest(player, farm);
                    return true;
                });
            panel.CloseButton();
            panel.Display();
        }

        private async Task Harvest(Player player, FarmPattern farm)
        {
            int characterId = player.character.Id;
            if (RemainingCooldown(player, farm) > 0) return;
            if (!Harvesting.Add(characterId))
            {
                player.Notify(farm.PatternName, "Vous récoltez déjà.", NotificationManager.Type.Warning, 4f);
                return;
            }

            try
            {
                Vector3 start = UberEatsPlugin.PositionOf(player);
                player.Notify(farm.PatternName, $"Récolte en cours… restez sur place ({farm.DurationSeconds} s).", NotificationManager.Type.Info, farm.DurationSeconds);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, farm.DurationSeconds)));

                if (UberEatsPlugin.FindPlayer(characterId) != player) return; // déconnecté entre-temps
                if (Vector3.Distance(start, UberEatsPlugin.PositionOf(player)) > MaxMoveDistance)
                {
                    player.Notify(farm.PatternName, "Récolte interrompue : vous avez bougé.", NotificationManager.Type.Error, 5f);
                    return;
                }

                int quantity = UnityEngine.Random.Range(farm.MinQuantity, farm.MaxQuantity + 1);
                if (!InventoryUtils.AddItem(player, farm.ItemId, quantity))
                {
                    player.Notify(farm.PatternName, "Inventaire plein !", NotificationManager.Type.Error, 5f);
                    return;
                }

                LastHarvest[(characterId, farm.Id)] = UberEatsPlugin.Now();
                player.Notify(farm.PatternName, $"+{quantity} {UberEatsPlugin.ItemName(player, farm.ItemId)}", NotificationManager.Type.Success, 5f);
            }
            finally
            {
                Harvesting.Remove(characterId);
            }
        }

        private static long RemainingCooldown(Player player, FarmPattern farm)
        {
            if (!LastHarvest.TryGetValue((player.character.Id, farm.Id), out long last)) return 0;
            return Math.Max(0, last + farm.CooldownSeconds - UberEatsPlugin.Now());
        }

        private static string FormatDuration(long seconds)
        {
            return seconds >= 60 ? $"{seconds / 60} min {seconds % 60:00} s" : $"{seconds} s";
        }

        // ------------------------------------------------------------------
        //  Côté staff : modèles et points
        // ------------------------------------------------------------------

        public async Task SetProperties(int id)
        {
            FarmPattern result = await Query(id);
            Id = id;
            TypeName = nameof(FarmPattern);
            PatternName = result?.PatternName;
        }

        private string Describe(Player player)
        {
            string item = ItemId == 0 ? Color("objet non défini", Colors.Error) : $"{UberEatsPlugin.ItemName(player, ItemId)} x{MinQuantity}-{MaxQuantity}";
            return $"[{Id}] {PatternName} - {item}" + (RestaurateurOnly ? Color(" (métier)", Colors.Info) : "");
        }

        /// <summary>Menu principal staff : choisir un modèle et placer un point de farm à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;
            List<FarmPattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Farm - Placer un point", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));
            panel.AddAction(Color("Nouveau modèle", Colors.Success), _ => SetPatternData(player));
            panel.AddAction("Modifier les modèles", async _ => await GetPatternData(player, true));
            panel.AddAction("Points placés", async _ => await GetNPoints(player));
            if (patterns.Count == 0)
                panel.AddTabLine(Color("Aucun modèle, créez-en un", Colors.Grey), _ => panel.Refresh());

            foreach (FarmPattern pattern in patterns)
            {
                panel.AddTabLine("Placer ici : " + pattern.Describe(player), async _ =>
                {
                    pattern.TypeName = nameof(FarmPattern);
                    pattern.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        player.Notify("Farm", $"Point « {pattern.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify("Farm", "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                    panel.Refresh();
                });
            }

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un modèle : on donne un nom puis on règle le reste.</summary>
        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Farm - Nouveau modèle", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom du point de farm (affiché aux joueurs)");
            panel.SetInputPlaceholder("Potager de tomates");

            panel.NextButton("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify("Farm", "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    SetPatternData(player);
                    return;
                }

                FarmPattern pattern = new FarmPattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    player.Notify("Farm", "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    return;
                }
                player.Notify("Farm", $"Modèle « {name} » créé : choisissez l'objet récolté.", NotificationManager.Type.Success, 5f);
                EditPattern(player, pattern.Id);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des modèles à modifier.</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<FarmPattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Farm - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => { });
            foreach (FarmPattern pattern in patterns)
                panel.AddTabLine(pattern.Describe(player), _ => EditPattern(player, pattern.Id));

            if (patterns.Count > 0) panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void EditPattern(Player player, int patternId)
        {
            FarmPattern pattern = await Query(patternId);
            if (pattern == null) return;

            Panel panel = Context.PanelHelper.Create($"Farm - {pattern.PatternName}", UIPanel.PanelType.Tab, player, () => EditPattern(player, patternId));
            string item = pattern.ItemId == 0 ? Color("à choisir", Colors.Error) : $"{UberEatsPlugin.ItemName(player, pattern.ItemId)} (id {pattern.ItemId})";

            panel.AddTabLine($"Nom : {pattern.PatternName}", _ => AskText(player, pattern));
            panel.AddTabLine($"Objet : {item}", _ => ChooseItem(player, patternId));
            panel.AddAction("Choisir l'objet (inventaire)", _ => ChooseItem(player, patternId));
            panel.AddAction("Choisir l'objet (id)", _ => AskItemId(player, patternId));
            panel.AddTabLine($"Quantité minimum : {pattern.MinQuantity}", _ => AskNumber(player, patternId, "Quantité minimum", 1, 1000, (p, v) => { p.MinQuantity = v; p.MaxQuantity = Math.Max(p.MaxQuantity, v); }));
            panel.AddTabLine($"Quantité maximum : {pattern.MaxQuantity}", _ => AskNumber(player, patternId, "Quantité maximum", 1, 1000, (p, v) => { p.MaxQuantity = v; p.MinQuantity = Math.Min(p.MinQuantity, v); }));
            panel.AddTabLine($"Durée de récolte : {pattern.DurationSeconds} s", _ => AskNumber(player, patternId, "Durée de récolte (secondes)", 0, 600, (p, v) => p.DurationSeconds = v));
            panel.AddTabLine($"Délai entre récoltes : {pattern.CooldownSeconds} s", _ => AskNumber(player, patternId, "Délai entre deux récoltes (secondes)", 0, 86400, (p, v) => p.CooldownSeconds = v));
            panel.AddTabLine("Réservé au métier : " + (pattern.RestaurateurOnly ? Color("oui", Colors.Success) : Color("non (tout le monde)", Colors.Warning)), async _ =>
            {
                pattern.RestaurateurOnly = !pattern.RestaurateurOnly;
                await pattern.Save();
                panel.Refresh();
            });
            panel.AddAction(Color("Supprimer le modèle", Colors.Error), async _ =>
            {
                await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                await pattern.Delete();
                player.Notify("Farm", $"Modèle « {pattern.PatternName} » supprimé.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskText(Player player, FarmPattern pattern)
        {
            Panel panel = Context.PanelHelper.Create("Farm - Renommer", UIPanel.PanelType.Input, player, () => AskText(player, pattern));
            panel.TextLines.Add($"Nouveau nom pour « {pattern.PatternName} »");
            panel.SetInputPlaceholder(pattern.PatternName);
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                FarmPattern fresh = await Query(pattern.Id);
                if (fresh == null) return false;
                fresh.PatternName = name;
                return await fresh.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskNumber(Player player, int patternId, string label, int min, int max, Action<FarmPattern, int> apply)
        {
            Panel panel = Context.PanelHelper.Create($"Farm - {label}", UIPanel.PanelType.Input, player, () => AskNumber(player, patternId, label, min, max, apply));
            panel.TextLines.Add($"{label} ({min} à {max})");
            panel.SetInputPlaceholder("Valeur");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int value) || value < min || value > max)
                {
                    player.Notify("Farm", $"Valeur invalide ({min} à {max}).", NotificationManager.Type.Error, 5f);
                    return false;
                }
                FarmPattern fresh = await Query(patternId);
                if (fresh == null) return false;
                apply(fresh, value);
                return await fresh.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Objet récolté : pris dans l'inventaire du staff, ou saisi par son id.</summary>
        private void ChooseItem(Player player, int patternId)
        {
            Dictionary<int, int> inventory = InventoryUtils.ReturnPlayerInventory(player);

            Panel panel = Context.PanelHelper.Create("Farm - Objet récolté", UIPanel.PanelType.TabPrice, player, () => ChooseItem(player, patternId));
            if (inventory.Count == 0)
                panel.AddTabLine("Inventaire vide", "", UberEatsPlugin.DefaultIcon, _ => panel.Refresh());

            foreach (int itemId in inventory.Keys)
            {
                panel.AddTabLine(UberEatsPlugin.ItemName(player, itemId), $"id {itemId}", ItemUtils.GetIconIdByItemId(itemId), async _ =>
                {
                    if (await SetItem(player, patternId, itemId)) panel.Previous();
                });
            }

            if (inventory.Count > 0) panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskItemId(Player player, int patternId)
        {
            Panel panel = Context.PanelHelper.Create("Farm - Id de l'objet", UIPanel.PanelType.Input, player, () => AskItemId(player, patternId));
            panel.TextLines.Add("Id de l'objet à récolter");
            panel.SetInputPlaceholder("Ex : 1112");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int itemId) || ItemUtils.GetItemById(itemId) == null)
                {
                    player.Notify("Farm", "Objet introuvable.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                return await SetItem(player, patternId, itemId);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> SetItem(Player player, int patternId, int itemId)
        {
            FarmPattern fresh = await Query(patternId);
            if (fresh == null) return false;
            fresh.ItemId = itemId;
            if (!await fresh.Save()) return false;
            player.Notify("Farm", $"Objet récolté : {UberEatsPlugin.ItemName(player, itemId)}.", NotificationManager.Type.Success, 4f);
            return true;
        }

        /// <summary>Liste de tous les points de farm placés ; choisir un point ouvre ses actions.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(FarmPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);

            Panel panel = Context.PanelHelper.Create("Farm - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));
            if (points.Count == 0)
                panel.AddTabLine(Color("Aucun point de farm placé", Colors.Grey), _ => { });

            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", _ => PointActions(player, point, name));
            }

            if (points.Count > 0) panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void PointActions(Player player, NPoint point, string name)
        {
            Panel panel = Context.PanelHelper.Create($"Farm - Point #{point.Id}", UIPanel.PanelType.Tab, player, () => PointActions(player, point, name));
            panel.AddInfo($"{name} - à {Mathf.RoundToInt(Vector3.Distance(UberEatsPlugin.PositionOf(player), point.VPosition))} m");
            panel.AddAction("Se téléporter", _ => Context.PointHelper.PlayerSetPositionToNPoint(player, point));
            panel.AddAction("Déplacer ici", async _ =>
            {
                if (await Context.PointHelper.SetNPointPosition(player, point))
                    player.Notify("Farm", "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddAction(Color("Supprimer", Colors.Error), async _ =>
            {
                await Context.PointHelper.DeleteNPoint(point);
                player.Notify("Farm", $"Point #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
