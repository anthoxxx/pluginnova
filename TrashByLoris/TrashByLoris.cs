using System.Collections.Generic;
using System.Threading.Tasks;
using Life;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.Utils;
using UnityEngine;
using Logger = ModKit.Internal.Logger;
using static ModKit.Helper.TextFormattingHelper;

namespace TrashByLoris
{
    /// <summary>
    /// Trash by Loris : les joueurs jettent leurs objets dans une poubelle, qu'elle soit
    /// posée par un joueur ou déjà présente sur la map. Les objets jetés sont retirés de
    /// l'inventaire et ne sont stockés nulle part (irrécupérables).
    /// </summary>
    public class TrashByLorisPlugin : ModKit.ModKit
    {
        private const string NotifyTitle = "Trash by Loris";

        /// <summary>ID des objets « poubelle » qui donnent accès au menu.</summary>
        private static readonly int[] TrashObjectIds = { 70, 1087, 1276, 1277, 1364, 1365 };

        /// <summary>Distance maximale (en mètres) pour utiliser une poubelle de la map.</summary>
        private const float MapTrashRange = 3f;

        public TrashByLorisPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "2.1.0", "Loris");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            if (AAMenu.AAMenu.menu == null)
            {
                Logger.LogError(PluginInformations.SourceName, "AAMenu introuvable : le plugin a besoin d'AAMenu pour fonctionner.");
                return;
            }

            // Une ligne dans le menu Proximité pour chaque modèle de poubelle :
            // AAMenu ne l'affiche que si le joueur est à côté d'un de ces objets.
            foreach (int objectId in TrashObjectIds)
            {
                AAMenu.Menu.AddProximityTabLine(PluginInformations, objectId, "Poubelle - Jeter des objets", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    if (player != null) OpenTrash(player);
                });
            }

            // Les poubelles de la map ne sont pas des objets posés : AAMenu ne les voit pas
            // dans Proximité. On ajoute donc une ligne dans Interactions qui vérifie elle-même
            // qu'une poubelle (de la map ou posée) est à côté du joueur.
            AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Poubelle", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                if (player == null) return;
                if (IsNearTrash(player))
                {
                    OpenTrash(player);
                }
                else
                {
                    player.Notify(NotifyTitle, "Aucune poubelle à proximité.", NotificationManager.Type.Warning, 5f);
                    AAMenu.AAMenu.menu.InteractionPanel(player);
                }
            });

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        private void OpenTrash(Player player)
        {
            Dictionary<int, int> inventory = InventoryUtils.ReturnPlayerInventory(player);

            Panel panel = PanelHelper.Create("Poubelle", UIPanel.PanelType.TabPrice, player, () => OpenTrash(player));
            panel.TextLines.Add(Color("Les objets jetés sont détruits définitivement.", Colors.Warning));

            if (inventory.Count == 0)
            {
                panel.AddTabLine("Votre inventaire est vide", _ => { });
            }
            else
            {
                foreach (KeyValuePair<int, int> slot in inventory)
                {
                    int itemId = slot.Key;
                    string itemName = GetItemName(player, itemId);

                    panel.AddTabLine(itemName, $"x{slot.Value}", ItemUtils.GetIconIdByItemId(itemId), _ =>
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

            Panel panel = PanelHelper.Create($"Jeter : {itemName}", UIPanel.PanelType.Input, player, () => AskQuantity(player, itemId, itemName));
            panel.TextLines.Add($"Combien de {itemName} voulez-vous jeter ? (vous en avez {owned})");
            panel.TextLines.Add(Color("Attention : les objets jetés sont détruits et ne pourront pas être récupérés.", Colors.Warning));
            panel.SetInputPlaceholder($"1 - {owned}");

            panel.PreviousButtonWithAction("Jeter", () =>
            {
                if (!int.TryParse(panel.inputText, out int quantity) || quantity <= 0)
                {
                    player.Notify(NotifyTitle, "Veuillez saisir une quantité valide.", NotificationManager.Type.Error, 5f);
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

        private bool ThrowItem(Player player, int itemId, string itemName, int quantity)
        {
            // Le joueur doit toujours être à côté d'une poubelle au moment de jeter
            if (!IsNearTrash(player))
            {
                player.Notify(NotifyTitle, "Vous devez être à côté d'une poubelle.", NotificationManager.Type.Error, 5f);
                return false;
            }

            if (quantity <= 0 || !InventoryUtils.CheckInventoryContainsItem(player, itemId, quantity))
            {
                player.Notify(NotifyTitle, "Vous n'avez pas autant d'objets.", NotificationManager.Type.Error, 5f);
                return false;
            }

            int removed = InventoryUtils.RemoveFromInventory(player, itemId, quantity);
            if (removed <= 0)
            {
                player.Notify(NotifyTitle, "Impossible de jeter cet objet.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify(NotifyTitle, $"Vous avez jeté {removed} {itemName}. Ils sont définitivement perdus.", NotificationManager.Type.Success, 5f);
            return true;
        }

        /// <summary>Le joueur est-il à côté d'une poubelle posée ou d'une poubelle de la map ?</summary>
        private bool IsNearTrash(Player player)
        {
            List<int> nearby = ProximityHelper.GetNearbyObjectsId(player);
            foreach (int objectId in TrashObjectIds)
            {
                if (nearby.Contains(objectId)) return true;
            }
            return IsNearMapTrash(player);
        }

        /// <summary>
        /// Poubelles déjà placées sur la map (celles du métier d'éboueur) : le jeu les
        /// enregistre dans Nova.server.trashes au démarrage du serveur.
        /// </summary>
        private static bool IsNearMapTrash(Player player)
        {
            if (player.setup == null || Nova.server.trashes == null) return false;

            Vector3 playerPos = ((Component)player.setup).transform.position;
            foreach (Life.VehicleSystem.Trash trash in Nova.server.trashes)
            {
                // Ignore les poubelles ramassées (invisibles) ou portées par un éboueur
                if (trash == null || !trash.isVisible || trash.isHoldedByPlayer) continue;

                if (Vector3.Distance(playerPos, ((Component)trash).transform.position) <= MapTrashRange)
                    return true;
            }
            return false;
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
    }
}
