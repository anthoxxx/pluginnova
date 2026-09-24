# pluginnova

Plugins serveur pour **Nova-Life: Amboise** (C#, Unity).

## Référence pour créer les plugins

Documentation officielle de l'API (version 1.69 - 1.69.1) :
**https://docs.team-nova.fr/api/Life**, et les guides : https://docs.team-nova.fr/guides

Toujours consulter ces pages avant d'écrire ou modifier un plugin, et respecter les
signatures, la structure et les conventions qui y sont décrites. Chaque classe a sa page :
`https://docs.team-nova.fr/api/<Namespace>.<Classe>` (ex. `Life.Plugin`, `Life.Network.Player`,
`Life.UI.UIPanel`). Le site renvoie 403 sans en-tête navigateur : utiliser `curl`.

## Formule de base d'un plugin

Un plugin est une classe qui dérive de `Life.Plugin` (classe abstraite), avec un
constructeur `Plugin(IGameAPI api)` à appeler via `base(api)` :

```csharp
using System;
using Life;
using Life.Network;          // Player, SChatCommand, LifeServer
using Life.UI;               // UIPanel
using Life.CheckpointSystem; // NCheckpoint, NVehicleCheckpoint
using UnityEngine;           // Debug, Vector3
using Newtonsoft.Json;       // JsonConvert

public class MonPlugin : Plugin
{
    public MonPlugin(IGameAPI api) : base(api) { }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        Debug.Log("MonPlugin chargé");
        // enregistrer les commandes et s'abonner aux évènements ici
    }
}
```

Champ disponible : `pluginsPath` (l'emplacement du plugin).

### Méthodes redéfinissables (`public override`) de `Plugin`

- `OnPluginInit()` : à l'initialisation du plugin
- `OnPlayerSpawnCharacter(Player, NetworkConnection, Characters)` : un joueur apparaît
- `OnPlayerDisconnect(NetworkConnection)` : un joueur se déconnecte
- `OnPlayerDeath(Player)` : un joueur meurt
- `OnPlayerText(Player, string)` : message dans le chat
- `OnPlayerInput(Player, KeyCode, bool)` / `OnPlayerInput(Player, KeyCode, Modifiers, bool)` : touche pressée
- `OnPlayerEnterArea(Player, AreaBox)` : entrée dans une zone
- `OnPlayerEnterVehicle(Vehicle, int, Player)` / `OnPlayerExitVehicle(Vehicle, Player)`
- `OnPlayerConsumeAlcohol(Player player, int itemId, float alcoholValue)`
- `OnPlayerSellDrugs(Player)`

Appeler `base.Methode(...)` pour conserver le comportement d'origine.

### S'abonner aux évènements du serveur

Les actions sont sur `Nova.server` (`LifeServer`). La méthode doit avoir les mêmes paramètres que l'`Action` :

```csharp
Nova.server.OnMinutePassedEvent += CustomOnMinutePassed;             // Action
Nova.server.OnPlayerReceiveItemEvent += CustomOnPlayerReceiveItem;   // Action<Player, int itemId, int slotId, int number>
Nova.server.OnPlayerReceiveItemEvent -= CustomOnPlayerReceiveItem;   // se désabonner
```

### Classe statique `Nova`

`Nova.server` (LifeServer), `Nova.man` (LifeManager, ex. `Nova.man.item.GetItem(id)`,
`Nova.man.newIcons`), `Nova.serverInfo`, `Nova.biz` (entreprises), `Nova.a` (terrains),
`Nova.v` (véhicules). Utilitaires : `ColorToHex`, `HexToColor`, `GenerateLicensePlate()`,
`GetMapName(int)`, `RemoveTextFormat(string)`, `UnixTimeNow()`, `UnixTimeToDateTime(long)`.

## Commandes de chat (`SChatCommand`)

```csharp
SChatCommand cmd = new SChatCommand("/test", new string[] { "/t" }, "Une commande de test", "/test <arg>",
    (Action<Player, string[]>)((player, args) =>
    {
        // args[0] = premier mot après la commande
        Debug.Log($"Commande déclenchée par {player.FullName}");
    }));
cmd.Register();
```

Le tableau d'alias est optionnel (constructeur à 4 paramètres sans alias).

## Checkpoints

```csharp
NCheckpoint cp = new NCheckpoint(player.netId, new Vector3(1, 2, 3), (triggered) => { /* ... */ });
player.CreateCheckpoint(cp);   // point bleu (piéton)

NVehicleCheckpoint vcp = new NVehicleCheckpoint(player.netId, new Vector3(1, 2, 3),
    (Action<NVehicleCheckpoint, uint>)((triggered, vehicleId) => { /* ... */ }));
player.CreateVehicleCheckpoint(vcp);   // point orange (conducteur)
```

## Menus (`UIPanel`)

Types : `UIPanel.PanelType.Text`, `Input`, `Tab`, `TabPrice`. Les méthodes s'enchaînent.

```csharp
UIPanel panel = new UIPanel("Titre", UIPanel.PanelType.Tab)
    .AddButton("Fermer", (p) => player.ClosePanel(p))
    .AddButton("Valider", (p) => p.SelectTab())
    .AddTabLine("Onglet n°1", (p) => { /* ... */ });
player.ShowPanelUI(panel);
```

- Text : `.SetText("...")`
- Input : `.SetText(...)`, `.SetInputPlaceholder(...)`, lire `p.inputText`, `panel.isPassword = true`
- Tab : `panel.selectedTab` donne l'onglet sélectionné
- TabPrice : `.AddTabLine(player.NewTranslate("Items", item.itemName), 10 + "€", Nova.man.newIcons.IndexOf(item.Icon), (p) => { })`
  (les noms d'items doivent être traduits avec `player.NewTranslate`)

## JSON

Newtonsoft : `JsonConvert.SerializeObject(obj, Formatting.Indented)` et
`JsonConvert.DeserializeObject<T>(json)`. À utiliser pour stocker des données ou les passer
dans des fonctions réseau.
