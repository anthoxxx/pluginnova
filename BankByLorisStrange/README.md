# Bank By Loris Strange

Plugin bancaire pour **Nova-Life: Amboise** (ModKit + AAMenu).

## Fonctionnalités

- **Frais bancaires sur les retraits**, configurables (pourcentage + fixe, minimum, maximum).
  - Sur les **DAB du plugin**, placés par le staff.
  - Sur les **DAB d'origine du jeu** : les frais sont prélevés juste après le retrait (désactivable).
- **Frais de dépôt** (optionnels) et **plafond de retrait par jour**.
- **Code de carte bleue pour chaque joueur** : il est généré automatiquement à sa première
  connexion et le joueur le reçoit en notification et dans le chat.
- **Item carte bancaire** choisi par le staff dans l'AAMenu : sans cet item, le DAB refuse l'accès.
- **Sécurité** : 3 codes faux → carte bloquée 10 min, et le DAB peut avaler la carte (réglable).
- **Opposition** (carte perdue / volée) et **commande d'une nouvelle carte** (payante, nouveau numéro).
- **Changement de code** au DAB, **historique** des opérations, numéros de carte valides (Luhn).
- **Frais reversés à une entreprise** (optionnel, id de l'entreprise).

## Installation

**DLL prête à l'emploi** : [`release/BankByLorisStrange.dll`](release/BankByLorisStrange.dll) → la copier dans le
dossier `Plugins` du serveur (avec ModKit et AAMenu), puis passer directement à l'étape 3.

Ou compiler soi-même :

1. Mettre les DLL dans `libs/` (voir [libs/README.md](libs/README.md)), puis `dotnet build -c Release`.
2. Copier `bin/Release/net472/BankByLorisStrange.dll` dans le dossier `Plugins` du serveur
   (avec ModKit et AAMenu).
3. Démarrer le serveur : `Plugins/ModKit/BankByLorisStrange/config.json` est créé.

## Staff

**AAMenu > Administration > Plugins > Bank By Loris Strange** (ou `/bank`, en service admin) :

| Menu | Rôle |
|---|---|
| Frais et réglages | Modifier toutes les valeurs du `config.json` en jeu |
| Item carte bancaire | Saisir l'ID de l'item qui sert de carte bleue (0 = pas de carte exigée) |
| Joueurs connectés | Voir le n° de carte et le code, générer un nouveau code, débloquer, donner une carte, historique |
| Générer les codes des joueurs connectés | Crée un code pour ceux qui n'en ont pas encore |
| Régénérer TOUS les codes | Nouveau code pour tout le monde (avec confirmation) |
| Placer un DAB à ma position | Raccourci vers la création des points DAB |
| Recharger config.json | Après une modification manuelle du fichier |

Pour placer des DAB : **AAMenu > Administration > Points bleus > DAB (Bank)** →
« Nouveau modèle » (ex. *DAB Banque Centrale*), puis choisir le modèle et « Placer ici ».

## Joueurs

- Se placer sur un point DAB → insérer la carte → taper son code →
  solde, retrait (montants rapides ou libre, frais affichés), dépôt, opérations, changer le code,
  commander une carte.
- `/macarte` (ou `/carte`, ou AAMenu > Interaction > Ma carte bancaire) : voir son numéro de carte
  et son code, faire opposition, commander une nouvelle carte.

## config.json

```json
{
  "WithdrawFeePercent": 2.0,       // % du montant retiré
  "WithdrawFeeFixed": 1.0,         // frais fixes par retrait
  "WithdrawFeeMin": 1.0,
  "WithdrawFeeMax": 250.0,         // 0 = pas de plafond
  "DepositFeePercent": 0.0,
  "DepositFeeFixed": 0.0,
  "DailyWithdrawLimit": 5000.0,    // DAB du plugin, 0 = illimité
  "ApplyFeesOnGameAtm": true,      // frais aussi sur les DAB du jeu
  "CardItemId": 0,                 // item carte bancaire (réglable dans l'AAMenu)
  "GiveCardOnAccountCreation": true,
  "NewCardPrice": 50.0,
  "MaxPinAttempts": 3,
  "BlockMinutes": 10,
  "SwallowCardOnBlock": true,
  "FeesBizId": 0,                  // entreprise qui touche les frais, 0 = aucune
  "AdminLevel": 1
}
```

(Les commentaires ci-dessus sont explicatifs : le vrai fichier JSON n'en contient pas.)

## Remarques

- Exemple de frais avec la config par défaut : retrait de 100 € → 1 € + 2 % = **3 €** de frais.
- DAB du jeu : le retrait se fait d'abord, les frais sont pris ensuite sur le compte du titulaire de
  la carte (ou en liquide si le compte est vide). Si le titulaire de la carte est déconnecté, le jeu
  ne prévient pas les plugins et aucun frais n'est appliqué. Le plafond journalier et le code du
  plugin ne s'appliquent qu'aux DAB du plugin (les DAB du jeu gardent leur propre code de carte).
- Les comptes, codes et l'historique sont dans la base SQLite de ModKit
  (tables `BankAccount`, `BankTransaction`, `AtmPattern`).
