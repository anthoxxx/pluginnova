# Contravention

Plugin Nova-Life : les membres d'une entreprise (ex. police municipale) donnent des
**contraventions aux véhicules**. Un véhicule verbalisé est **verrouillé** tant que toutes
ses contraventions ne sont pas payées.

## Installation
- ModKit et AAMenu doivent être installés sur le serveur.
- Copier `release/Contravention.dll` dans le dossier `Plugins` du serveur, démarrer une fois.
- Dans `Plugins/Contravention/config.json`, mettre l'id de l'entreprise dans `BizId`, puis redémarrer.

## Utilisation
- **Agent** : menu AA → Entreprise → **Contraventions** → donner une contravention (véhicule à
  moins de 10 m, montant, motif) ou voir/annuler les contraventions impayées.
- **Joueur** : menu AA → Interaction → **Mes contraventions** → payer (argent liquide).
  Les contraventions des véhicules à proximité sont aussi affichées.

Les contraventions sont stockées dans `Plugins/Contravention/fines.json`.

## Compatibilité
Compilé contre `Assembly-CSharp.dll` 1.2.1.5. Le verrouillage utilise le même mécanisme que le
jeu (`NetworkisLocked` + `RpcOnLockVehicle`), le paiement `Player.AddMoney(-montant, motif)`.

## Compilation
Copier les DLL listées dans `libs/README.md` dans `libs/`, puis `dotnet build -c Release`.
