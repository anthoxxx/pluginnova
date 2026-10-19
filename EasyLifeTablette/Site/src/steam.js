// Connexion « Se connecter avec Steam » (OpenID 2.0) + profil via la Steam Web API
const OPENID_URL = 'https://steamcommunity.com/openid/login';
const CLAIMED_ID = /^https:\/\/steamcommunity\.com\/openid\/id\/(\d{17})$/;

function loginUrl(baseUrl) {
  const params = new URLSearchParams({
    'openid.ns': 'http://specs.openid.net/auth/2.0',
    'openid.mode': 'checkid_setup',
    'openid.return_to': `${baseUrl}/auth/steam/retour`,
    'openid.realm': baseUrl,
    'openid.identity': 'http://specs.openid.net/auth/2.0/identifier_select',
    'openid.claimed_id': 'http://specs.openid.net/auth/2.0/identifier_select',
  });
  return `${OPENID_URL}?${params}`;
}

/**
 * Vérifie la réponse de Steam auprès de Steam lui-même (check_authentication).
 * Renvoie le SteamID64 ou null si la signature n'est pas valide.
 */
async function verify(query, baseUrl) {
  if (query['openid.mode'] !== 'id_res') return null;
  if (query['openid.return_to'] !== `${baseUrl}/auth/steam/retour`) return null;
  const match = CLAIMED_ID.exec(query['openid.claimed_id'] || '');
  if (!match) return null;

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (key.startsWith('openid.') && typeof value === 'string') params.set(key, value);
  }
  params.set('openid.mode', 'check_authentication');

  const res = await fetch(OPENID_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: params,
  });
  const body = await res.text();
  return /is_valid\s*:\s*true/.test(body) ? match[1] : null;
}

/** Pseudo + avatar Steam (nécessite une clé Steam Web API, sinon renvoie null). */
async function fetchProfile(apiKey, steamId) {
  if (!apiKey) return null;
  try {
    const url = `https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key=${encodeURIComponent(apiKey)}&steamids=${steamId}`;
    const res = await fetch(url);
    const data = await res.json();
    const p = data?.response?.players?.[0];
    return p ? { pseudo: p.personaname || '', avatar: p.avatarfull || '' } : null;
  } catch (e) {
    console.warn('Profil Steam indisponible :', e.message);
    return null;
  }
}

module.exports = { loginUrl, verify, fetchProfile };
