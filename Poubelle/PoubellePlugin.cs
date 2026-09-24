using System;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using ModKit.Helper;
using ModKit.Interfaces;
using ModKit.Internal;
using Poubelle.Entities;

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
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, nameof(TrashPattern), pattern, this);
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
