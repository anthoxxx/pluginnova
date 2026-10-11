# Contravention

Plugin Nova-Life : les membres d'une entreprise (police, fourrière…) **verbalisent les
véhicules proches**. Le véhicule est **bloqué** tant que son propriétaire n'a pas payé.

## Installation
1. ModKit et AAMenu doivent être installés sur le serveur.
2. Copier **`Release/Contravention.dll`** dans le dossier `Plugins` du serveur.
3. Démarrer une fois le serveur, puis ouvrir `Plugins/Contravention/config.json` :
   ```json
   { "BizId": 0, "MaxAmount": 100000, "VehicleRadius": 10.0 }
   ```
   Mettre dans `BizId` l'ID de l'entreprise autorisée à verbaliser, puis redémarrer.
   Les contraventions impayées sont enregistrées dans `Plugins/Contravention/fines.json`.

## Utilisation
- **Agent** : menu AA → Entreprise → **Contraventions** → *Donner une contravention*
  (véhicules à moins de `VehicleRadius` m) → montant → motif. *Contraventions impayées* permet
  de voir ou d'annuler une contravention.
- **Propriétaire** : prévenu à la verbalisation et à chaque connexion. Menu AA → Interaction →
  **Mes contraventions** → *Payer* (argent liquide). Le véhicule est débloqué quand toutes ses
  contraventions sont payées.
- **Blocage** : toutes les secondes, le véhicule est verrouillé, son moteur coupé, et un
  joueur qui monte dedans en est sorti.

## Diagnostic
Au démarrage, les logs affichent des lignes `[Contravention] ...` listant les membres du jeu
trouvés (argent, plaque, propriétaire, verrou, moteur). Si le paiement, la plaque ou le verrou
ne marchent pas, envoyer ces lignes.

## Recompiler
Copier les DLL listées dans `libs/README.md` dans `libs/`, puis `dotnet build -c Release`.
