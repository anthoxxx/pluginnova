# PermisArme

Examen du permis de port d'arme sur des **points bleus natifs** (ModKit) placés par le staff.

## Compilation
1. Copier les 7 DLL listées dans `libs/README.md` dans `PermisArme/libs/`.
2. Ouvrir `PermisArme.csproj`, générer en **Release**.
3. Copier `bin/Release/net472/PermisArme.dll` dans le dossier `Plugins` du serveur.

## Placer les points (staff, en service admin)
- AAMenu → **Administration → Points bleus → Type de point : Permis d'arme**, ou
- AAMenu → Administration → Plugins → **Permis d'arme (config) → Points bleus d'examen**, ou
- commande **`/permisarmepoint`**.

Puis **Nouveau modèle** (nom du point) → choisir le modèle → **Placer ici**.
**Points** : se téléporter / déplacer / supprimer. Les points sont stockés dans
`Plugins/ModKit/data.sqlite` et réapparaissent à chaque connexion.

## Joueurs
Marcher dans le point bleu → examen. `/monpermisarme` : voir son permis.
Police : `/permisarme` (registre, retrait).

## Taille du texte
`TailleTexte` (en %, défaut 70) dans `PermisArme/config.json`, ou depuis le menu config.
Les questions longues sont coupées sur plusieurs lignes (`CaracteresParLigne`, défaut 55).
