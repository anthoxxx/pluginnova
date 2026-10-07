# PNJ by Loris Strange (PNJCreator)

**Créé par Loris Strange.**

Plugin serveur Nova-Life: Amboise pour peupler la ville de commerçants, gardes, PNJ de métier
ou personnages d'ambiance — statiques ou en patrouille — avec une apparence sur mesure et une
IA conversationnelle qui discute en RP avec les joueurs.

- Chaque PNJ est un vrai personnage networké (prefab joueur du jeu), visible par tous.
- Plugin autonome : **un seul DLL**, aucune dépendance (ni ModKit, ni AAMenu, ni Harmony).
- Exécuté côté serveur, sur le thread principal (les requêtes IA tournent en arrière-plan,
  leur résultat est appliqué sur le thread principal).
- Aucun retrait / ajout d'inventaire, aucun impact économique.

## Installation

1. Compiler (voir plus bas) ou récupérer `PNJCreator.dll`.
2. Copier `PNJCreator.dll` dans le dossier `Plugins` du serveur.
3. Démarrer le serveur : le dossier `Plugins/PNJCreator/` et ses fichiers sont créés.
4. En jeu (admin) : `/Configpnjcreator` → **Fournisseurs d'IA** → coller la clé API.

## Commandes

| Commande | Qui | Rôle |
|---|---|---|
| `/Configpnjcreator` (alias `/configpnj`, `/pnjcreator`) | admin | Éditeur complet des PNJ |
| `/say <message>` | joueur | Parler au PNJ IA le plus proche (dans sa portée de discussion) |
| `/pnjreload` | admin | Recharge les fichiers JSON à chaud (respawn des PNJ) |
| `/pnjdebug` | admin | Infos de debug du PNJ le plus proche (< 15 m) |

L'accès admin (niveau requis, service admin obligatoire ou non) se règle dans
**Réglages** ou dans `npcs.config.json` → `Admin`.

## Éditeur (`/Configpnjcreator`)

- **Créer un PNJ à ma position** : nom, sexe → le PNJ apparaît à votre position et orientation,
  avec votre apparence comme base.
- **Liste des PNJ** → fiche du PNJ :
  - renommer, sexe Homme / Femme, échelle (0,5 à 2) ;
  - déplacer à ma position, tourner vers moi, rotation en degrés ;
  - **Apparence** : copier mon apparence, modifier la tenue (haut, bas, chaussures, chapeau,
    accessoire — les vêtements du kiosque avec leurs icônes), flocage (lien d'une image pour
    le haut / le bas), coupe de cheveux, barbe, couleur des cheveux, morphologie (taille,
    corpulence, musculature, minceur, silhouette, poitrine, tête). Chaque clic est appliqué
    en direct sur le PNJ ;
  - **Déplacement** : statique ou mobile, points de passage (ajoutés à votre position),
    vitesse, pause à chaque point, parcours en boucle ou aller-retour ;
  - **IA** : activer / désactiver, personnalité (prompt), portée de discussion, portée
    d'écoute, effacer la mémoire ;
  - faire réapparaître, supprimer.
- **Fournisseurs d'IA** : OpenAI, DeepSeek, Anthropic. Clé API (saisie masquée, affichée
  partiellement), liste des modèles récupérée depuis l'API du fournisseur, modèle
  personnalisé, adresse de l'API, fournisseur actif.
- **Consommation de l'IA** : tokens en entrée / sortie et nombre de requêtes, au total, par
  fournisseur et par PNJ (détail par fournisseur). Réinitialisation en un clic.
- **Réglages** : accès admin, portées par défaut, taille de la mémoire, délai entre deux
  `/say`, instructions communes de l'IA, couleur des noms.

Chaque changement est sauvegardé immédiatement dans le fichier JSON.

## Fichiers (`Plugins/PNJCreator/`)

| Fichier | Contenu |
|---|---|
| `npcs.config.json` | Réglages, fournisseurs d'IA (clés !), PNJ (position, apparence, IA, patrouille) |
| `memory.json` | Mémoire des conversations : par PNJ, puis par joueur (`steamId:idPersonnage`) |
| `usage.json` | Consommation de tokens |

Les valeurs invalides sont corrigées automatiquement au chargement (portées, échelle,
fournisseur inconnu, identifiants en double…). Un fichier JSON illisible est copié en
`*.broken-<date>` puis remplacé par les valeurs par défaut.

`MenuIcons` (optionnel) permet de choisir l'icône d'une entrée de menu avec l'id ou le slug
d'un item du jeu, ex. `"MenuIcons": { "ai": "12", "usage": "phone" }`. Clés utilisées :
`create, list, ai, usage, settings, reload, male, female, rename, scale, teleport, rotate,
appearance, move, respawn, delete, copy, outfit, custom, hair, beard, haircolor, morpho,
cloth_Shirt, cloth_Pants, cloth_Shoes, cloth_Hat, cloth_Accessory, waypoint, speed, wait,
route, prompt, range, memory, provider, key, model, npc, admin, color`. Sans réglage, les
menus d'apparence utilisent les icônes des vêtements / coupes du jeu.

## IA conversationnelle

- Le joueur tape `/say Bonjour !` près d'un PNJ dont l'IA est activée.
- Les joueurs dans la **portée d'écoute** voient la phrase du joueur puis la réponse du PNJ.
- Le PNJ reçoit : les instructions communes, son nom et son sexe, sa personnalité, puis
  l'historique avec ce joueur (mémoire) → réponses courtes, en français, en personnage.
- Un seul message en attente par joueur, avec un délai réglable entre deux `/say`.

Modèles par défaut : `gpt-4o-mini` (OpenAI), `deepseek-chat` (DeepSeek),
`claude-opus-5-5` (Anthropic). Ils se changent dans le menu (la liste vient de l'API).
Pour Anthropic, sur les modèles récents (Opus 5 / 5.5, Fable 5, Sonnet 5.5), le plugin
demande un effort de réflexion bas (répliques courtes) et active le repli automatique côté
serveur (`fallbacks: "default"`) si le modèle refuse une demande. `claude-haiku-5-5` est
une option bien moins chère pour de simples PNJ d'ambiance.

## Fonctionnement technique

- Le PNJ est une instance du prefab joueur du jeu (`LifeNetworkManager.malePrefab` /
  `femalePrefab`) créée avec `NetworkServer.Spawn`, **sans connexion ni objet `Player`** :
  il n'apparaît pas dans la liste des joueurs et ne déclenche aucun évènement joueur.
- Neutralisation : arrêt des tâches périodiques joueur (`SecondUpdate` / `MinuteUpdate`),
  désactivation des contrôleurs de déplacement / d'entrée côté serveur, rigidbody
  cinématique, santé / faim / soif à 100.
- Apparence : `CharacterCustomizationSetup` du jeu (SyncVar + `RpcSkinChange`), donc
  appliquée en direct et visible par les joueurs qui se connectent ensuite.
- Patrouille : le serveur déplace le PNJ et passe `NetworkTransform` / `NetworkAnimator`
  en synchronisation serveur → clients ; les paramètres d'animation `ForwardSpeed` /
  `HorizontalSpeed` sont réglés pendant la marche.
- Déplacement / rotation / échelle / sexe : le PNJ est recréé (position garantie pour tous).
- Les PNJ apparaissent `SpawnDelaySeconds` (10 s par défaut) après le démarrage du serveur.

À tester sur votre serveur en priorité : la marche en patrouille (fluidité, animation) dépend
du composant de synchronisation réseau du prefab joueur ; `/pnjdebug` affiche son mode.

## Compilation

1. Copier les 5 DLL listées dans `libs/README.md` dans `PNJCreator/libs/`.
2. Ouvrir **`PNJCreator.csproj`** (Visual Studio) ou lancer `dotnet build -c Release`.
3. Copier `bin/Release/net472/PNJCreator.dll` dans le dossier `Plugins` du serveur.
