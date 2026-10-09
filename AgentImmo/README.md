# Agent Immo - By Loris Strange

Plugin Nova-Life : les entreprises **agent immobilier** vendent et louent des terrains aux
joueurs. Tout passe par **AAMenu**, et chaque opération (vente, location, prolongation, fin de
bail, achat direct en jeu, ajout ou retrait d'un bien, changement de prix...) est enregistrée
dans un **historique complet**, avec envoi sur Discord en option.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `AgentImmo/libs/`.
2. Ouvrir **`AgentImmo.csproj`** (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/AgentImmo.dll` dans le dossier `Plugins` du serveur.

## Mise en place (staff)
Au premier démarrage, le plugin crée l'activité **« Agent Immobilier »**. Pour qu'une
entreprise devienne une agence immobilière, au choix :
- AAMenu → **Administration → Activités → Agent Immobilier** → ajouter l'entreprise, ou
- AAMenu → **Administration → Plugins → Agent Immo → Agences autorisées** → saisir l'ID de l'entreprise.

## Agents immobiliers
Menu AAMenu → **Métier → Agence immobilière** (ou `/immo` → *Espace agent immobilier*) :
- **Ajouter un terrain au catalogue** : ID du terrain (pré-rempli avec le terrain où vous êtes),
  nom (adresse du terrain par défaut), type (vente, location ou les deux), prix de vente et/ou loyer par jour. Votre position
  sert de point GPS pour les clients.
- **Catalogue de l'agence** : faire une offre à un joueur proche, modifier nom / description /
  type / prix / GPS, retirer du catalogue, remettre en vente, supprimer.
- **Offre** : le client reçoit un menu avec le détail et paie **en espèces** ou **par carte**.
  Le terrain lui est attribué automatiquement. L'agent touche sa **commission** (10 % par
  défaut), le reste va sur le **compte de l'entreprise**.
- **Demandes des clients** : demandes d'achat, de location ou de visite envoyées depuis le
  catalogue. Il est possible d'envoyer une offre à distance si le client est en ligne.
- **Locations en cours** : locataire, date de fin, total payé, mettre fin au bail.
- **Historique** (filtrable par type) et **statistiques** : chiffre d'affaires, ventes,
  locations, commissions, meilleur agent.

## Joueurs
Menu AAMenu → **Interaction → Agence immobilière** (ou `/immo`) :
- **Biens disponibles** : catalogue de toutes les agences, GPS jusqu'au terrain, demande
  d'achat / location / visite (les agents en ligne sont prévenus).
- **Mes locations** : date de fin, **prolonger** (paiement espèces ou carte), résilier.
- **Historique** de ses achats et locations.

Une alerte est envoyée au locataire avant la fin du bail (24 h par défaut). À la fin, le
terrain revient à son propriétaire précédent et le bien redevient disponible.

## Administration
AAMenu → **Administration → Plugins → Agent Immo** :
historique complet de toutes les agences, tous les biens, toutes les locations, statistiques
globales, agences autorisées, paramètres et **infos d'un terrain**.

Paramètres (modifiables en jeu ou dans `Plugins/ModKit/AgentImmo/config.json`) :

| Option | Défaut | Rôle |
|---|---|---|
| `ActivityName` | `Agent Immobilier` | Activité AAMenu qui donne accès au menu agent |
| `AllowedBizIds` | `[]` | Entreprises autorisées en plus de l'activité |
| `CommissionPercent` | `10` | Commission de l'agent sur chaque vente / location |
| `MaxRentDays` | `30` | Durée maximale d'un bail (jours) |
| `ExpiryWarningHours` | `24` | Alerte au locataire avant la fin du bail |
| `OfferDistance` | `5` | Distance max. (m) pour faire une offre à un joueur |
| `AdminLevelMin` | `1` | Niveau admin pour le menu d'administration |
| `MaxLogsDisplayed` | `100` | Lignes d'historique affichées |
| `AutoTransferTerrain` | `true` | Donne / retire automatiquement le terrain en jeu |
| `DiscordWebhookUrl` | vide | Webhook Discord recevant toutes les opérations |

Les données sont dans la base ModKit (`Plugins/ModKit/data.sqlite`) : tables
`ImmoProperty`, `ImmoRental`, `ImmoLog`, `ImmoRequest`.

## Fonctionnement des terrains
Le plugin utilise la même méthode que l'achat d'un terrain dans le jeu : il change
`permissions.owner` du terrain (`Nova.a.GetAreaById`) puis l'enregistre (`LifeArea.Save()`).
- **Vente** : le terrain est donné au client.
- **Location** : le terrain est donné au locataire pendant le bail, puis rendu à son ancien
  propriétaire (joueur ou entreprise) à la fin.
- Paiement du client : espèces (`AddMoney`) ou carte (`AddBankMoney`). La commission va dans
  le portefeuille de l'agent (ou sa banque s'il est plein), le reste sur le compte de
  l'entreprise (`Bizs.AddBankMoney`).
- Le GPS utilise la position enregistrée par l'agent, sinon la position du terrain dans le jeu.

**Administration → Agent Immo → Infos d'un terrain** affiche l'adresse, le propriétaire, le
prix du jeu, et l'état du terrain dans le catalogue.

Si `AutoTransferTerrain` est désactivé, ou si un terrain est introuvable, la transaction est
quand même enregistrée avec la mention **« ATTRIBUTION MANUELLE REQUISE »** et le staff donne
le terrain à la main.
