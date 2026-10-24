# Whitelist.nova

**by Loris Strange & Matheo Mercier**

Plugin Nova-Life: Amboise de whitelist avec questionnaire, validation par le staff et code
de validation unique.

## Fonctionnement

1. **Connexion** : chaque joueur non whitelisté est téléporté sur un **lieu d'attente** choisi
   au hasard parmi ceux définis par le staff, et il est **gelé**. S'il s'éloigne quand même
   (distance configurable), il est ramené.
2. **Questionnaire** : un panel s'ouvre et le joueur répond aux questions (réponse libre ou
   choix multiple). Il voit un récapitulatif avant d'envoyer. Il peut le rouvrir avec `/wl`.
3. **Alerte staff** : à l'envoi, le staff connecté reçoit une notification en jeu et un
   message est envoyé sur **Discord** (webhook), avec toutes les réponses.
4. **Examen** : le staff ouvre la candidature (AAMenu → Whitelist, ou `/wladmin`), lit les
   réponses, puis **Accepte** ou **Refuse** (avec une raison si besoin).
5. **Code** : s'il est accepté, le joueur reçoit un **code de validation à 4 chiffres**
   (ex. `4821`). Deux joueurs qui n'ont pas encore saisi leur code n'ont jamais le même. Il est dégelé et peut rejoindre le
   **point bleu de validation**.
6. **Validation** : le joueur saisit son code sur le point bleu → il est **téléporté** à la
   destination choisie par le staff pour ce point et devient whitelisté pour toujours
   (sauf si le staff révoque sa whitelist).

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `Whitelist/libs/`.
2. Ouvrir **`Whitelist.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/Whitelist.nova.dll` dans le dossier `Plugins` du serveur.

## Mise en place (staff)
Ouvrir **AAMenu → Administration → Plugins → Whitelist** (ou `/wladmin`) :

| Menu | Rôle |
|---|---|
| **Candidatures en attente** | Lire une candidature, l'accepter (génère le code) ou la refuser. |
| **Historique** | Candidatures traitées : revoir le code, en générer un nouveau, révoquer. |
| **Questionnaire** | Ajouter / modifier / monter / descendre / supprimer des questions. Pour chaque question : intitulé, type (réponse libre ou choix multiple), choix proposés (séparés par `;`), obligatoire ou non, longueur minimale, texte d'aide. |
| **Lieux d'attente** | « Ajouter ma position » pour créer un lieu où les joueurs non whitelistés sont bloqués. Plusieurs lieux possibles (choisi au hasard). |
| **Points de saisie du code** | **Nouveau modèle** : votre position actuelle devient la destination des joueurs validés. Ensuite, allez près des lieux d'attente et faites **Placer ici** pour poser le point bleu. **Modèles** : renommer, « Destination ici », TP destination, supprimer. **Points** : TP, déplacer, supprimer. |
| **Destination après le code (téléportation)** | Liste des points de validation et de leur destination. Allez à l'endroit voulu, sélectionnez le point puis **Ma position** : les joueurs qui valident leur code sur ce point y seront téléportés. **Se téléporter** pour vérifier. |
| **Joueurs connectés** | Whitelister directement un joueur (sans code) ou révoquer sa whitelist. |
| **Paramètres** | Tout le reste (voir ci-dessous). |

Le point de saisie est aussi disponible dans **AAMenu → Administration → Points bleus →
Whitelist - Saisie du code**.

> Placez le point de saisie du code à moins de *Distance max.* (30 m par défaut) d'un lieu
> d'attente, ou n'importe où : les joueurs acceptés ont aussi le droit d'être près d'un
> point de saisie.

Un questionnaire d'exemple est créé au premier démarrage : modifiez-le ou supprimez-le.

## Tester seul (mode test)
**/wladmin → Tester le plugin sur moi** : vous faites le parcours complet d'un nouveau
joueur, même en étant staff.

Le menu vérifie d'abord la configuration ([OK] / [X]) : questions, lieux d'attente,
modèle de point avec destination, point de saisie placé, webhook Discord (optionnel).
Le bouton **Tester Discord** envoie un message de test sur le webhook.

1. **Démarrer le test** : vous êtes téléporté et gelé sur un lieu d'attente, le questionnaire s'ouvre.
2. Répondez et envoyez : l'alerte arrive en jeu et sur Discord (candidature marquée `[TEST]`).
3. `/wladmin` → **Candidatures en attente** → acceptez votre candidature `[TEST]` : le code s'affiche.
4. Allez sur le point bleu de validation et entrez le code.
5. Vous êtes téléporté à la **destination choisie pour ce point** : test terminé.

**Arrêter le test** (même menu) vous libère à tout moment.

## Paramètres
Modifiables en jeu (**Paramètres**) ou dans `Plugins/Whitelist.nova/config.json`
(puis « Recharger config.json ») :

| Clé | Défaut | Description |
|---|---|---|
| `Enabled` | `true` | Active la whitelist. |
| `DiscordWebhookUrl` | `""` | Webhook Discord des alertes (vide = désactivé). |
| `DiscordMention` | `""` | Mention envoyée avec chaque nouvelle candidature (`@here`, `<@&ID_ROLE>`). |
| `DiscordColor` | `#3498db` | Couleur de l'embed des candidatures. |
| `DiscordLogDecisions` | `true` | Logue aussi acceptations, refus, validations et révocations. |
| `FormTitle` | `Candidature Whitelist` | Titre du questionnaire. |
| `WelcomeText` / `EndText` / `AcceptedText` | … | Messages d'accueil, de fin et d'acceptation (`\n` = retour à la ligne). |
| `CodePrefix` / `CodeLength` | `""` / `4` | Préfixe et longueur du code de validation. |
| `CodeDigitsOnly` | `true` | Code uniquement en chiffres (sinon lettres + chiffres). |
| `MaxCodeAttempts` | `5` | Essais ratés avant 60 s de blocage. |
| `FreezePlayers` | `true` | Gèle les joueurs en attente (dégelés une fois acceptés). |
| `MaxDistance` | `30` | Distance max. (m) autour du lieu d'attente / du point de code. |
| `CheckIntervalSeconds` | `5` | Fréquence de la vérification de distance. |
| `SpawnDelaySeconds` | `3` | Délai après l'apparition avant la téléportation. |
| `StaffBypass` | `true` | Les admins ne sont pas bloqués. |
| `AllowRetryAfterRefuse` | `true` | Un joueur refusé peut renvoyer une candidature. |
| `MinAdminLevel` | `1` | Niveau admin requis pour gérer la whitelist. |
| `HistorySize` | `50` | Nombre de candidatures affichées dans l'historique. |

## Commandes
- `/wl` (`/whitelist`) : joueur → ouvrir le questionnaire / voir l'état de sa candidature / revoir son code.
- `/wladmin` : staff → menu de gestion.

## Données
Questions, candidatures, lieux d'attente et points sont dans la base ModKit
(`Plugins/ModKit/data.sqlite`). La whitelist est liée au **compte** du joueur
(tous ses personnages).
