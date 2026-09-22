src/server connects the banking core to Vintage Story’s authoritative server environment.  It manages startup and
shutdown, configuration loading, world-save storage, game-clock readings, authenticated player observations, permissions,
Banker sessions, and incoming network requests. It performs physical inventory operations and recovery, statement
printing, exact withdrawal-Max planning, Charter and natural branch topology/worldgen, protected claims, and Banker
behavior. It supplies trusted inputs to the core, persists recovery artifacts, and sends only selected player-safe
results and notifications to clients.
