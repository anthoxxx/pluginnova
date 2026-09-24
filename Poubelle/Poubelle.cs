using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace Poubelle
{
    /// <summary>
    /// Plugin de poubelles : le staff place des points bleus où les joueurs
    /// peuvent jeter définitivement les items de leur inventaire.
    /// </summary>
    public class PoubellePlugin : ModKit.ModKit
    {
        public PoubellePlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "anthoxxx");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Orm.RegisterTable<TrashPattern>();

            // Le même modèle sert au PointHelper (déclenchement des points)
            // et à AAMenu (Administration > Points bleus).
            TrashPattern pattern = new TrashPattern(false);
            PointHelper.AddPattern(nameof(TrashPattern), pattern);
            if (AAMenu.AAMenu.menu != null)
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Poubelle", pattern, this);
            else
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /poubelle.");

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            // Affiche au joueur toutes les poubelles enregistrées
            PointHelper.InitAllNPoint(player);
        }

        private void RegisterCommands()
        {
            // Raccourci staff pour ouvrir directement le menu de création des poubelles
            SChatCommand command = new SChatCommand("/poubelle", "Gérer les poubelles (staff)", "/poubelle",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!player.IsAdmin || !player.serviceAdmin)
                    {
                        player.Notify("Poubelle", "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    new TrashPattern(false) { Context = this }.CreateOrGenerate(player);
                }));
            command.Register();
        }
    }
}

namespace Poubelle
{
    /// <summary>
    /// Modèle de poubelle. Un modèle peut être placé autant de fois que voulu :
    /// chaque placement crée un point bleu (NPoint) à la position du staff.
    /// Les items jetés sont supprimés de l'inventaire et ne sont stockés nulle part :
    /// il est donc impossible de les récupérer.
    /// </summary>
    public class TrashPattern : ModEntity<TrashPattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(TrashPattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public TrashPattern() { }

        public TrashPattern(bool isCreated)
        {
            TypeName = nameof(TrashPattern);
        }

        // ------------------------------------------------------------------
        //  Côté joueur : utilisation de la poubelle
        // ------------------------------------------------------------------

        public void OnPlayerTrigger(Player player)
        {
            OpenTrash(player);
        }

        private void OpenTrash(Player player)
        {
            Dictionary<int, int> inventory = InventoryUtils.ReturnPlayerInventory(player);

            Panel panel = Context.PanelHelper.Create(PatternName ?? "Poubelle", UIPanel.PanelType.TabPrice, player, () => OpenTrash(player));

            if (inventory.Count == 0)
            {
                panel.AddTabLine("Votre inventaire est vide", _ => { });
            }
            else
            {
                foreach (KeyValuePair<int, int> slot in inventory)
                {
                    int itemId = slot.Key;
                    int quantity = slot.Value;
                    string itemName = GetItemName(player, itemId);

                    panel.AddTabLine(itemName, $"x{quantity}", ItemUtils.GetIconIdByItemId(itemId), _ =>
                    {
                        AskQuantity(player, itemId, itemName);
                    });
                }
                panel.NextButton("Jeter", () => panel.SelectTab());
            }

            panel.CloseButton();
            panel.Display();
        }

        private void AskQuantity(Player player, int itemId, string itemName)
        {
            int owned = CountItem(player, itemId);

            Panel panel = Context.PanelHelper.Create($"Jeter : {itemName}", UIPanel.PanelType.Input, player, () => AskQuantity(player, itemId, itemName));
            panel.TextLines.Add($"Combien de {itemName} voulez-vous jeter ? (vous en avez {owned})");
            panel.TextLines.Add(Color("Attention : les objets jetés sont détruits et ne pourront pas être récupérés.", Colors.Warning));
            panel.SetInputPlaceholder($"1 - {owned}");

            panel.PreviousButtonWithAction("Jeter", () =>
            {
                if (!int.TryParse(panel.inputText, out int quantity) || quantity <= 0)
                {
                    player.Notify("Poubelle", "Veuillez saisir une quantité valide.", NotificationManager.Type.Error, 5f);
                    return Task.FromResult(false);
                }
                return Task.FromResult(ThrowItem(player, itemId, itemName, quantity));
            });
            panel.PreviousButtonWithAction("Tout jeter", () =>
                Task.FromResult(ThrowItem(player, itemId, itemName, CountItem(player, itemId))));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static bool ThrowItem(Player player, int itemId, string itemName, int quantity)
        {
            if (quantity <= 0 || !InventoryUtils.CheckInventoryContainsItem(player, itemId, quantity))
            {
                player.Notify("Poubelle", "Vous n'avez pas autant d'objets.", NotificationManager.Type.Error, 5f);
                return false;
            }

            int removed = InventoryUtils.RemoveFromInventory(player, itemId, quantity);
            if (removed <= 0)
            {
                player.Notify("Poubelle", "Impossible de jeter cet objet.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify("Poubelle", $"Vous avez jeté {removed} {itemName}. Ils sont définitivement perdus.", NotificationManager.Type.Success, 5f);
            return true;
        }

        private static int CountItem(Player player, int itemId)
        {
            return InventoryUtils.ReturnPlayerInventory(player).TryGetValue(itemId, out int quantity) ? quantity : 0;
        }

        private static string GetItemName(Player player, int itemId)
        {
            Item item = ItemUtils.GetItemById(itemId);
            return item != null ? player.NewTranslate("Items", item.itemName) : $"Objet #{itemId}";
        }

        // ------------------------------------------------------------------
        //  Côté staff : gestion des modèles et des points (AAMenu / /poubelle)
        // ------------------------------------------------------------------

        public async Task SetProperties(int id)
        {
            TrashPattern result = await Query(id);
            Id = id;
            TypeName = nameof(TrashPattern);
            PatternName = result?.PatternName;
        }

        /// <summary>Menu principal staff : choisir un modèle et placer une poubelle à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;

            List<TrashPattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Poubelles - Placer une poubelle", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
            {
                panel.AddTabLine("Aucun modèle, créez-en un", _ => { });
            }
            foreach (TrashPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(TrashPattern);
                    pattern.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        player.Notify("Poubelle", $"Poubelle « {pattern.PatternName} » placée à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify("Poubelle", "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
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

        /// <summary>Création d'un nouveau modèle de poubelle.</summary>
        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create("Poubelles - Nouveau modèle", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom de la poubelle (affiché aux joueurs)");
            panel.SetInputPlaceholder("Poubelle");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify("Poubelle", "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                TrashPattern pattern = new TrashPattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    player.Notify("Poubelle", "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                player.Notify("Poubelle", $"Modèle « {name} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des modèles : renommer ou supprimer (avec tous ses points).</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<TrashPattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create("Poubelles - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
            {
                panel.AddTabLine("Aucun modèle", _ => { });
            }
            foreach (TrashPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(TrashPattern);
                    pattern.Context = Context;

                    if (action == "rename")
                    {
                        RenamePattern(player, pattern);
                    }
                    else if (action == "delete")
                    {
                        await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                        if (await pattern.Delete())
                            player.Notify("Poubelle", $"Modèle « {pattern.PatternName} » et ses poubelles supprimés.", NotificationManager.Type.Success, 5f);
                        else
                            player.Notify("Poubelle", "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
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

        private void RenamePattern(Player player, TrashPattern pattern)
        {
            Panel panel = Context.PanelHelper.Create("Poubelles - Renommer", UIPanel.PanelType.Input, player, () => RenamePattern(player, pattern));
            panel.TextLines.Add($"Nouveau nom pour « {pattern.PatternName} »");
            panel.SetInputPlaceholder(pattern.PatternName ?? "Poubelle");

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

        /// <summary>Liste de toutes les poubelles placées : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(TrashPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("Poubelles - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
            {
                panel.AddTabLine("Aucune poubelle placée", _ => { });
            }
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
                                player.Notify("Poubelle", "Poubelle déplacée à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            player.Notify("Poubelle", $"Poubelle #{point.Id} supprimée.", NotificationManager.Type.Success, 5f);
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
