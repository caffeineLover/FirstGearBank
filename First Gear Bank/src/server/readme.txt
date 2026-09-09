src/server connects the banking core to Vintage Story’s authoritative server environment.  It manages startup and
shutdown, configuration loading, world-save storage, game-clock readings, authenticated player observations, permissions,
Banker sessions, and incoming network requests.  It performs physical inventory operations and recovery, manages
server-side branch and Banker behavior, and sends safe results and notifications to clients. It supplies trusted inputs
to the core and applies approved outcomes through the game’s APIs.