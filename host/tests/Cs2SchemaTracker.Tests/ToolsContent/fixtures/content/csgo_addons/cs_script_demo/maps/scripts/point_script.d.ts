/**
 * This file, `point_script.d.ts`, documents the JavaScript API for cs_script scripts attached to point_script entities.
 * This file is a TypeScript Declaration file. https://www.typescriptlang.org/docs/handbook/2/type-declarations.html#dts-files
 * This file can be used by various editors to provide tooling while editing JavaScript. https://www.typescriptlang.org/docs/handbook/intro-to-js-ts.html 
 * Next to this file is a `tsconfig.json` file configured for editing JavaScript targetting the current version used by CS2.
 * Place copies of these two files, `point_script.d.ts` and `tsconfig.json`, next to your scripts and some editors will begin providing tooling without further configuration.
 * These two files will be maintained as the cs_script API changes or the JavaScript version in CS2 is updated.
 * Please send feedback to CSGOTeamFeedback@valvesoftware.com with "cs_script Feedback" in the subject line.
 */

/**
 * `"cs_script/point_script"` is the module provided to scripts loaded for point_script entities.
 * 
 * # Setup:
 * - Create a JavaScript file (.js) that imports this module.
 *      - See `hello.js` for an example.
 * - Create a point_script entity in your map and set its cs_script field to reference your JavaScript file as a vjs asset.
 *      - See example addon cs_script_demo. There is a point_script entity in the cs_script_demo.vmap named "hello_cs_script" that runs `hello.js`. There are a handful of other examples as well.
 * 
 * # Execution:
 * - The compiled version of your script (.vjs_c) will be loaded during map load.
 * - When the point_script entity is spawned it will execute all code at the top level scope of your script.
 * - Register callbacks on `Instance` to setup code that executes at various times throughout the lifetime of the map.
 *      - A function passed to `Instance.OnActivate` will be invoked when the point_script entity is activated.
 *      - A function passed to `Instance.OnPlayerJump` will be invoked when any player in the map jumps.
 *
 * # Tips:
 * - Entity variables are stable. Two variables referring to the same entity will be reference equals (===).
 * - Extra values attached to an entity variable will still be there if the variable is fetched again.
 * - A map can have multiple point_script entities. Each script will run with its own Instance, set of globals, and set of entity variables.
 * 
 * # Tools Mode:
 * - In tools mode, saving changes to your script will recompile your file, clear all registered callbacks, and re-run the top level scope of your script.
 * - Global variables and instances of entity variables will persist across reloads.
 *      - Beware. This is an avenue for holding references to code from previous iterations of your script. This is only a concern in tools mode.
 * - See `Instance.OnScriptReload` for a tool to handle edge cases around reloading.
 */
declare module "cs_script/point_script"
{
    export const Instance: IPointScript;
    
    export const Console: IConsole;

    export const GameRules: IGameRules;

    type Vector = { x: number, y: number, z: number };
    type RotationVector = { x: number, y: number, z: number };
    type QAngle = { pitch: number, yaw: number, roll: number };
    type ColorArg = { r: number, g: number, b: number, a?: number };
    type Color = { r: number, g: number, b: number, a: number };
    type InputValue = boolean | number | string | Vector | Color | undefined;

    /**
     * The API provided to scripts attached to a point_script entity.
     * Access these functions by importing Instance from "cs_script/point_script".
     */
    interface IPointScript {
        /** Print some text to the game window. Only works in dev environments. */
        DebugScreenText(config: { text: any, x: number, y: number, duration?: number, color?: ColorArg }): void;
        /** Draw a line in the world. Only works in dev environments. */
        DebugLine(config: { start: Vector, end: Vector, duration?: number, color?: ColorArg }): void;
        /** Draw a wire sphere in the world. Only works in dev environments. */
        DebugSphere(config: { center: Vector, radius: number, duration?: number, color?: ColorArg }): void;
        /** Draw an axis aligned box in the world. Only works in dev environments. */
        DebugBox(config: { mins: Vector, maxs: Vector, duration?: number, color?: ColorArg }): void;

        /**
         * Called in Tools mode when the script is reloaded due to changes.
         * The before callback will be invoked before pre-load teardown.
         * The after callback will be invoked after the new script is evaluated and will be passed the return value of the before callback.
         */
        OnScriptReload<T>(config: { before?: () => T, after?: (memory: T) => void }): void;

        /**
         * Writes save data associated with this workshop addon.
         * Will synchronously write to disk every time this is called.
         */
        SetSaveData(data: string): void;
        /**
         * Retrieves the save data associated with this workshop addon.
         * Will synchronously read from disk the first time this is called.
         */
        GetSaveData(): string;

        /** Called at a specified time. Control when this is run using SetNextThink. */
        SetThink(callback: () => void): void;
        /** Set when the OnThink callback should next be run. The exact time will be on the tick nearest to the specified time, which may be earlier or later. */
        SetNextThink(time: number): void;
        /** Returns a promise that resolves after `delay` number of seconds. The exact time will be on the tick nearest to the specified delay, which may be earlier or later. */
        Delay(delay: number): Promise<void>;

        /**
         * Queue up a callback to be invoked once, after all entities have executed their think functions this tick (eg. player input has been handled, projectiles have moved).
         * This can be useful for delaying until a clean moment when an entity isn't mid-computation and might ignore or misinterpret.
         * This can be useful for delaying until the world is in a consistent state.
         * Callbacks queued up during a post entity think callback will be invoked in the same tick.
         */
        QueueAfterThinks( callback: () => void ): void;

        /** Called when the point_script entity is activated */
        OnActivate(callback: () => void): void;
        /** Called when input RunScriptInput is triggered on the point_script entity with a parameter value that matches name. */
        OnScriptInput(name: string, callback: (inputData: { caller?: Entity, activator?: Entity }) => void): void;

        /** Called when a client finishes the initial connection handshake with the server. */
        OnPlayerConnect(callback: (event: { player: CSPlayerController }) => void): void;
        /** Called when a client finishes loading and is ready to spawn. */
        OnPlayerActivate(callback: (event: { player: CSPlayerController }) => void): void;
        /** Called when a client disconnects from the server. */
        OnPlayerDisconnect(callback: (event: { playerSlot: number }) => void): void;
        /** Called when a player respawns, changes team, or is placed back at spawn due to a round restart */
        OnPlayerReset(callback: (event: { player: CSPlayerPawn }) => void): void
        /** Called when a player changes team */
        OnPlayerTeamChanged(callback: (event: { player: CSPlayerPawn, oldTeam: number }) => void): void
        /** Called when a new round begins */
        OnRoundStart(callback: () => void): void;
        /** Called when a team wins a round */
        OnRoundEnd(callback: (event: { winningTeam: number, reason: CSRoundEndReason }) => void): void;
        /** Called at the start of cleanup for a round restart */
        OnBeginRoundRestart(callback: () => void): void;
        /** Called when a player starts planting their c4 */
        OnBombPlantStart(callback: (event: { planter: CSPlayerPawn }) => void): void;
        /** Called when a player aborts planting their c4 */
        OnBombPlantAbort(callback: (event: { planter: CSPlayerPawn }) => void): void;
        /** Called when a player plants their c4 */
        OnBombPlant(callback: (event: BombPlantEvent) => void): void;
        /** Called when a player starts defusing a c4 */
        OnBombDefuseStart(callback: (event: { plantedC4: CSPlantedC4 }) => void): void;
        /** Called when a player aborts defusing a c4 */
        OnBombDefuseAbort(callback: (event: { plantedC4: CSPlantedC4 }) => void): void;
        /** Called when a player defuses a c4 */
        OnBombDefuse(callback: (event: BombDefuseEvent) => void): void;
        /** Called when a c4 explodes */
        OnBombExplode(callback: (event: { plantedC4: CSPlantedC4 }) => void): void;
        /** Called when a player starts picking up a hostage */
        OnHostagePickUpStart(callback: (event: { hostage: CSHostage }) => void): void;
        /** Called when a player aborts picking up a hostage */
        OnHostagePickUpAbort(callback: (event: { hostage: CSHostage, player: CSPlayerPawn }) => void): void;
        /** Called when a player finishes picking up a hostage */
        OnHostagePickUp(callback: (event: { hostage: CSHostage }) => void): void;
        /** Called when a carried hostage is dropped without being rescued */
        OnHostageDrop(callback: (event: { hostage: CSHostage, dropper: CSPlayerPawn }) => void): void;
        /** Called when a hostage reaches a rescue zone */
        OnHostageRescue(callback: (event: { hostage: CSHostage }) => void): void;
        /** Called when a hostage is killed */
        OnHostageKill(callback: (event: { hostage: CSHostage, attacker?: CSPlayerPawn }) => void): void;
        /**
         * Called immediately before a CSPlayerPawn takes damage to armor and health.
         * Called after hitgroup modifications are applied such as headshot multiplier.
         * This won't be called if the player would take no damage. Such as if they're frozen or invulnerable or if friendly fire would disable the damage.
         * @param callback
         * Return `{ damage: N }` to modify the amount of damage.
         * Return `{ damageFlags: event.damageFlags | CSDamageFlags.IGNORE_ARMOR }` to have the damage pierce armor.
         * Return `{ abort: true }` to cancel the damage event.
         */
        OnModifyPlayerDamage(callback: (event: ModifyPlayerDamageEvent) => ModfiyPlayerDamageResult | void): void;
        /** 
         * Called when a player has taken damage.
         */
        OnPlayerDamage(callback: (event: PlayerDamageEvent) => void): void;
        /** Called when a player dies. `inflictor`, `attacker` and `weapon` will match the damage event that caused the kill. */
        OnPlayerKill(callback: (event: { player: CSPlayerPawn, inflictor?: Entity, attacker?: Entity, weapon?: CSWeaponBase }) => void): void;
        /** Called when a player jumps off the ground. */
        OnPlayerJump(callback: (event: { player: CSPlayerPawn }) => void): void;
        /** Called when a player hits the ground while falling. */
        OnPlayerLand(callback: (event: { player: CSPlayerPawn }) => void): void;
        /** Called when a player sends a chat message. `team` will match they player's team if the message was sent to team chat. */
        OnPlayerChat(callback: (event: { player?: CSPlayerController, text: string, team: number }) => void): void;
        /** Called when a player pings a location. */
        OnPlayerPing(callback: (event: { player: CSPlayerController, position: Vector }) => void): void;
        /** Called when a gun is reloaded. */
        OnGunReload(callback: (event: { weapon: CSWeaponBase }) => void): void;
        /** Called when a gun emits bullets. A shotgun will only trigger this once when emitting multiple bullets at once. */
        OnGunFire(callback: (event: { weapon: CSWeaponBase }) => void): void;
        /**
         * Called when a bullet hits a surface.
         * Penetrations can cause a single bullet to trigger multiple impacts.
         * This will be called for all impacts of a bullet before any player damage events are called.
         */
        OnBulletImpact(callback: (event: { weapon: CSWeaponBase, position: Vector, hitEntity?: Entity }) => void): void;
        /** Called when a weapon is dropped. */
        OnWeaponDrop(callback: (event: { weapon: CSWeaponBase, dropper: CSPlayerPawn }) => void): void;
        /** Called when a weapon is picked up. */
        OnWeaponPickup(callback: (event: { weapon: CSWeaponBase }) => void): void;
        /** Called when a grenade is thrown. `projectile` is the newly created grenade projectile. */
        OnGrenadeThrow(callback: (event: { weapon: CSWeaponBase, projectile: CSGrenadeProjectileBase }) => void): void;
        /** Called when a grenade bounces. */
        OnGrenadeBounce(callback: (event: GrenadeBounceEvent) => void): void;
        /** Called when a knife attacks, even if it misses. */
        OnKnifeAttack(callback: (event: { weapon: CSWeaponBase, attackType: CSWeaponAttackType }) => void): void;
        /** Called when a button in a CustomHudLayout is clicked. */
        OnCustomHudClicked( callback: (event: {player: CSPlayerController, layout: CustomHudLayout, buttonId: string}) => void ): void;

        /** Fire the input on all targets matching the specified names. */
        EntFireAtName(config: { name: string, input: string, value?: InputValue, caller?: Entity, activator?: Entity, delay?: number }): void;
        /** Fire the input on the specified target. */
        EntFireAtTarget(config: { target: Entity, input: string, value?: InputValue, caller?: Entity, activator?: Entity, delay?: number }): void;
        /** Connect the output of an entity to a callback. The return value is a connection id that can be used in `DisconnectOutput` */
        ConnectOutput(target: Entity, output: string, callback: (inputData: { value?: InputValue, caller?: Entity, activator?: Entity }) => any): number | undefined;
        /** Find entities by name. */
        DisconnectOutput(connectionId: number): void;

        /** Find the first entity matching the specified name. */
        FindEntityByName(name: string): Entity | undefined;
        /** Find entities matching the specified name. */
        FindEntitiesByName(name: string): Entity[];
        /** Find the first entity of the specified class name. */
        FindEntityByClass(className: string): Entity | undefined;
        /** Find entities of the specified class name. */
        FindEntitiesByClass(className: string): Entity[];
        /** Get the player controller in the given slot. */
        GetPlayerController(playerSlot: number): CSPlayerController | undefined;
        /** Get all the player controllers. Includes disconnected players. */
        GetAllPlayerControllers(): CSPlayerController[];

        /** Trace a point along a line and detect collisions */
        TraceLine(trace: BaseTraceConfig): TraceResult;
        /** Trace a sphere along a line and detect collisions */
        TraceSphere(trace: { radius: number } & BaseTraceConfig): TraceResult;
        /** Trace an axis aligned bounding box along a line and detect collisions */
        TraceBox(trace: { mins: Vector, maxs: Vector } & BaseTraceConfig): TraceResult;
        /** Trace as a player would collide */
        TracePlayer(trace: PlayerTrace): PlayerTraceResult;
        /** Trace as a bullet and detect hits and damage */
        TraceBullet(trace: BulletTrace): BulletTraceResult[];

        /** Get if the server is a dedicated server. */
        IsDedicatedServer(): boolean;
        /** Get the game time in seconds. */
        GetGameTime(): number;
        /** Get the current Game Type. */
        GetGameType(): number;
        /** Get the current Game Mode. */
        GetGameMode(): number;
        /** Get the name of the current map. */
        GetMapName(): string;

        /** Spawns a live grenade projectile. */
        SpawnGrenadeProjectile(config: SpawnGrenadeProjectileConfig): CSGrenadeProjectileBase;

        /**
         * Loads resources on the server and clients to avoid hitches when later referenced.
         * Supports .vmdl, .vpcf, .vmat, and .vsnd resources.
         * @experimental This feature is experimental and may experience breaking changes.
         * Please send feedback to CSGOTeamFeedback@valvesoftware.com with "cs_script Feedback" in the subject line.
         */
        Precache(config: { resources: string[] }): CSPrecache | undefined;

        /** @deprecated This method will be removed in a future update */
        OnBeforePlayerDamage(callback: () => any): void;
        /** @deprecated This method will be removed in a future update */
        OnBeforeReload(callback: () => any): void;
        /** @deprecated This method will be removed in a future update */
        OnReload(callback: (memory: any) => void): void;
        /** @deprecated This overload will be removed in a future update */
        DebugScreenText(text: any, x: number, y: number, duration: number, color: ColorArg): void;
        /** @deprecated This overload will be removed in a future update */
        DebugLine(start: Vector, end: Vector, duration: number, color: ColorArg): void;
        /** @deprecated This overload will be removed in a future update */
        DebugBox(mins: Vector, maxs: Vector, duration: number, color: ColorArg): void;
        /** @deprecated This overload will be removed in a future update */
        DebugSphere(center: Vector, radius: number, duration: number, color: ColorArg): void;
        /** @deprecated This overload will be removed in a future update */
        EntFireAtName(name: string, input: string, inputData?: InputValue | { value?: InputValue, caller?: Entity, activator?: Entity }, delay?: number): void;
        /** @deprecated This overload will be removed in a future update */
        EntFireAtTarget(target: Entity, input: string, inputData?: InputValue | { value?: InputValue, caller?: Entity, activator?: Entity }, delay?: number): void;
        /** @deprecated This method will be removed in a future update */
        Msg(text: any): void;
        /** @deprecated This method will be removed in a future update */
        ClientCommand(playerSlot: number, command: string): void;
        /** @deprecated This method will be removed in a future update */
        ServerCommand(command: string): void;
        /** @deprecated This method will be removed in a future update */
        RegisterCheatCommand(name: string, callback: (args: string) => void): void;
        /** @deprecated This method will be removed in a future update */
        IsWarmupPeriod(): boolean;
        /** @deprecated This method will be removed in a future update */
        IsFreezePeriod(): boolean;
        /** @deprecated This method will be removed in a future update */
        IsTeamIntroPeriod(): boolean;
        /** @deprecated This method will be removed in a future update */
        GetRoundsPlayed(): number;
        /** @deprecated This method will be removed in a future update */
        GetRoundRemainingTime(): number;
        /** @deprecated This method will be removed in a future update */
        SetRoundRemainingTime(time: number): void;
        /** @deprecated This method will be removed in a future update */
        AddTeamMoney(team: number, reason: CSTeamMoneyReason, amount?: number): void;
    }
    
    /**
     * An API provided to scripts for the console: logging, ConVars, and ConCommands.
     * Access these functions by importing Console from "cs_script/point_script".
     */
    interface IConsole
    {
        /** Log a message to the console. */
        Log(text: any): void;

        /** Get the value of a boolean convar. */
        GetConvarBoolean(name: string): boolean | undefined;
        /** Get the value of a numeric convar. */
        GetConvarNumber(name: string): number | undefined;
        /** Get the value of any convar, converted to a string. */
        GetConvarString(name: string): string | undefined;
        /** Get the value of a Vector convar. */
        GetConvarVector(name: string): Vector | undefined;
        /** Get the value of a QAngle convar. */
        GetConvarQAngle(name: string): QAngle | undefined;
        /** Get the value of a Color convar. */
        GetConvarColor(name: string): Color | undefined;
        /** Set the value of a convar. */
        SetConvar(name: string, value: boolean | number | string | Vector | QAngle | ColorArg): void;

        /** Issue the specified command to the specified client. */
        ClientCommand(playerSlot: number, command: string): void;
        /** Issue a command. */
        ServerCommand(command: string): void;
        /** Creates a console command that will run the specified callback. The command will only work when sv_cheats is true. */
        RegisterCheatCommand(name: string, callback: (args: string) => void): void;
    }

    /**
     * An API provided to scripts for the state of the match: warmup, rounds, and team money.
     * Access these functions by importing GameRules from "cs_script/point_script".
     */
    interface IGameRules
    {
        /** Get if the game is currently in a Warmup period. */
        IsWarmupPeriod(): boolean;
        /** Start the warmup period, restarting the round. */
        StartWarmup(): void;
        /** End the warmup period as if its timer had run out. The game mode then ends it the usual way, starting on the next tick. */
        EndWarmup(): void;

        /** Get if the game is currently in a Freeze period. */
        IsFreezePeriod(): boolean;
        /** Get if the game is currently in a Team Intro period. */
        IsTeamIntroPeriod(): boolean;
        /** Get the number of rounds played in the current game. */

        GetRoundsPlayed(): number;
        /** Get the time remaining in the current round in seconds. */
        GetRoundRemainingTime(): number;
        /** Set the time remaining in the current round in seconds. */
        SetRoundRemainingTime(time: number): void;
        /** Get the team's score. */
        GetTeamScore(team: number): number;
        /** Add to the team's score. Call before TerminateRound so the round end sees the new score. */
        AddTeamScore(team: number, score: number): void;
        /** Terminate the current round. Counts as a round played unless it is a draw. Will not add to team scores. Does nothing if the round has already ended. */
        TerminateRound(reason: CSRoundEndReason, delay?: number): void;

        /**
         * Adds the money to all player controllers on the team, following all classic rules.
         * If players are not eligible to receive end-of-round money, then that restriction is honored.
         * Disconnected players will get their account balance incremented, and will have correct
         * amount of money after they reconnect to the server.
         * If the `amount` parameter is 0 or undefined, then the amount will be determined using server convar
         * setting corresponding to the reason parameter.
         */
        AddTeamMoney(team: number, reason: CSTeamMoneyReason, amount?: number): void;
    }

    export enum CSRoundEndReason {
        UNKNOWN = -1,
        IN_PROGRESS,
        GAME_COMMENCING,
        DRAW,
        TARGET_BOMBED,
        TARGET_SAVED,
        BOMB_DEFUSED,
        HOSTAGES_RESCUED,
        HOSTAGES_NOT_RESCUED,
        CTS_WIN,
        TERRORISTS_WIN,
        CTS_SURRENDER,
        TERRORISTS_SURRENDER,
    }

    export enum CSTeamMoneyReason {
        /** $0 */
        NONE,
        /** cash_team_terrorist_win_bomb */
        TERRORIST_WIN_BOMB,
        /** cash_team_elimination_hostage_map_t */
        ELIMINATION_HOSTAGE_MAP_T,
        /** cash_team_elimination_hostage_map_ct */
        ELIMINATION_HOSTAGE_MAP_CT,
        /** cash_team_elimination_bomb_map */
        ELIMINATION_BOMB_MAP,
        /** cash_team_win_by_time_running_out_hostage */
        WIN_BY_TIME_RUNNING_OUT_HOSTAGE,
        /** cash_team_win_by_time_running_out_bomb */
        WIN_BY_TIME_RUNNING_OUT_BOMB,
        /** cash_team_win_by_defusing_bomb */
        WIN_BY_DEFUSING_BOMB,
        /** cash_team_win_by_hostage_rescue */
        WIN_BY_HOSTAGE_RESCUE,
        /** cash_team_loser_bonus */
        LOSER_BONUS,
        /** cash_team_rescued_hostage */
        RESCUED_HOSTAGE,
        /** cash_team_hostage_alive */
        HOSTAGE_ALIVE,
        /** cash_team_planted_bomb_but_defused */
        PLANTED_BOMB_BUT_DEFUSED,
        /** cash_team_hostage_interaction */
        HOSTAGE_INTERACTION,
        /** cash_team_bonus_shorthanded */
        BONUS_SHORTHANDED,
    }

    export enum CSWeaponType {
        KNIFE,
        PISTOL,
        SUBMACHINEGUN,
        RIFLE,
        SHOTGUN,
        SNIPER_RIFLE,
        MACHINEGUN,
        C4,
        TASER,
        GRENADE,
        EQUIPMENT,
        STACKABLEITEM, // Healthshot
        UNKNOWN,
    }

    export enum CSDamageFlags {
        NONE = 0,
        SUPPRESS_HEALTH_CHANGES = 1 << 0,
        SUPPRESS_PHYSICS_FORCE = 1 << 1,
        SUPPRESS_EFFECTS = 1 << 2,
        PREVENT_DEATH = 1 << 3,
        FORCE_DEATH = 1 << 4,
        SUPPRESS_DAMAGE_MODIFICATION = 1 << 5,
        IGNORE_ARMOR = 1 << 6,
    }

    interface BaseTraceConfig {
        start: Vector;
        end: Vector;
        /** Specify entities to not trace against. 0, 1 or 2 entities is equally fast. 3 or more is equally slower */
        ignoreEntity?: Entity | Entity[];
        ignorePlayers?: boolean;
        /** Trace against hitboxes instead of the larger collision shape for entities with hitboxes (eg. players) */
        traceHitboxes?: boolean;
    }

    interface TraceResult {
        fraction: number;
        end: Vector;
        didHit: boolean;
        startedInSolid: boolean;
        normal: Vector;
        hitEntity?: Entity;
        hitGroup?: CSHitGroup;
    }

    /**
     * Configuration object for `Instance.TraceBullet`
     * @example {damage:30, rangeModifer:.85, penetration:1} // Glock
     * @example {damage:30, rangeModifer:.45, penetration:1} // Mag-7
     * @example {damage:36, rangeModifier:.98, penetration:2} // AK47
     * @example {damage:115, rangeModifier:.99, penetration:2.5} // AWP
     */
    interface BulletTrace {
        start: Vector,
        end: Vector,
        /** The player shooting the bullet. Acts as `ignoreEnt` for traces. */
        shooter: CSPlayerPawn,
        /** The starting damage value of the bullet. This will reduce as it travels through the air and penetrates solids. @default 100 */
        damage?: number,
        /** The exponential damage drop off constant from traveling through air. @default .85 */
        rangeModifier?: number,
        /** The power to maintain damage during penetration. Will default to 1 if left undefined. @default 1 */
        penetration?: number,
    }

    /**
     * Result entry for `Instance.TraceBullet`
     */
    interface BulletTraceResult {
        /** The entity hit by the bullet. This is not limited to players. */
        hitEntity: Entity;
        /** Damage value reduced by travel, before damage modification (body armor, headhshots, etc) */
        damage: number;
        position: Vector;
        hitGroup: CSHitGroup;
    }

    interface ModifyPlayerDamageEvent {
        /** The victim that is taking damage */
        player: CSPlayerPawn;
        /** The amount of damage being applied, after hitgroup modifications and before armor modifications */
        damage: number;
        /** The types of damage. */
        damageTypes: CSDamageTypes;
        /** The flags configuring how to interpret the damage. */
        damageFlags: CSDamageFlags;
        /** The hit group where the damage occured. */
        hitGroup: CSHitGroup;
        /** The entity applying the damage. For bullets this is the owner of the gun. For grenades this is the exploding projectile. */
        inflictor?: Entity;
        /** The entity credited with causing the damage. For bullets this is the shooter. For grenades this is the thrower. */
        attacker?: Entity;
        /** The weapon used. For grenades this will not be present because the weapon is often removed before the projectile explodes. */
        weapon?: CSWeaponBase;
    }

    interface ModfiyPlayerDamageResult {
        /** If true, stop processing this damage */
        abort?: boolean;
        /** The amount of damage being applied, before armor and hitgroup modifications */
        damage?: number;
        /** The type or types of damage. */
        damageTypes?: CSDamageTypes;
        /** The flags configuring how to interpret the damage. */
        damageFlags?: CSDamageFlags;
    }


    type SpawnGrenadeProjectileConfig = SpawnGrenadeProjectileConfigWithOwner | SpawnGrenadeProjectileConfigWithoutOwner;

    interface SpawnGrenadeProjectileConfigWithOwner {
        type: CSGrenadeType;
        thrower: CSPlayerPawn;
        /* defaults to 1, full strength. */
        throwStrength?: number;
        /* defaults to thrower's throw position */
        position?: Vector;
        /* defaults to {0,0,0} */
        angles?: QAngle;
        /* defaults to thrower's throw velocity */
        velocity?: Vector;
        /* defaults to a {600,rand(-1200,1200),0} */
        angularVelocity?: RotationVector;
    }

    interface SpawnGrenadeProjectileConfigWithoutOwner {
        type: CSGrenadeType;
        /* position is required if no thrower is specified */
        position: Vector;
        /* defaults to {0,0,0} */
        angles?: QAngle;
        /* defaults to {0,0,0} */
        velocity?: Vector;
        /* defaults to {0,0,0} */
        angularVelocity?: RotationVector;
    }

    export class CSPrecache {
        IsServerLoading(): boolean;
        IsServerLoaded(): boolean;
        IsClientLoading(playerSlot: number): boolean;
        IsClientLoaded(playerSlot: number): boolean;
        IsLoadedForAll(): boolean;

        Release(): void;
    }

    /**
     * The base class for all entities
     */
    export class Entity {
        /** Returns `false` if the entity has been deleted. */
        IsValid(): boolean;
        /** The position of the origin of this entity relative to the world. */
        GetAbsOrigin(): Vector;
        /** The position of the origin of this entity relative to its parent. Will be relative to the world if no parent. */
        GetLocalOrigin(): Vector;
        /** The angles (pitch, yaw, and roll) of this entity relative to the world. */
        GetAbsAngles(): QAngle;
        /** The angles (pitch, yaw, and roll) of this entity relative to its parent. Will be relative to the world if no parent. */
        GetLocalAngles(): QAngle;
        /** The velocity of this entity relative to the world. */
        GetAbsVelocity(): Vector;
        /** The velocity of this entity relative to its parent. Will be relative to the world if no parent. */
        GetLocalVelocity(): Vector;
        /** The angular velocity of this entity relative to the world. */
        GetAbsAngularVelocity(): RotationVector;
        /** The angular velocity of this entity relative to its parent. Will be relative to the world if no parent. */
        GetLocalAngularVelocity(): RotationVector;
        /** The angles of the eyes of this entity relative to the world. */
        GetEyeAngles(): QAngle;
        /** The position of the eyes of this entity relative to the world */
        GetEyePosition(): Vector;
        /** Update the physics state of this entity. */
        Teleport(newValues: { position?: Vector, angles?: QAngle, velocity?: Vector, angularVelocity?: RotationVector }): void;
        /** Move this entity without resetting the client's interpolation history. */
        Move(newValues: { position?: Vector, angles?: QAngle, velocity?: Vector, angularVelocity?: RotationVector }): void;
        GetMoveType(): CSMoveType;
        SetMoveType(moveType: CSMoveType): void;
        GetClassName(): string;
        GetEntityName(): string;
        SetEntityName(name: string): void;
        GetOwner(): Entity | undefined;
        SetOwner(owner: Entity | undefined): void;
        GetParent(): Entity | undefined;
        SetParent(parent: Entity | undefined): void;
        GetTeamNumber(): number;
        GetHealth(): number;
        SetHealth(health: number): void;
        GetMaxHealth(): number;
        SetMaxHealth(health: number): void;
        IsAlive(): boolean;
        /** Get if this is the world entity */
        IsWorld(): boolean;
        /** Get the entity that this entity is resting on. Will be `undefined` if in the air. */
        GetGroundEntity(): Entity | undefined;
        /** Apply damage to this entity. Damage value will be modified by armor and hitgroup. */
        TakeDamage(takeDamage: EntityDamage): number;
        Kill(): void;
        Remove(): void;

        /** @deprecated This overload will be removed in a future update */
        Teleport(newPosition: Vector | null, newAngles: QAngle | null, newVelocity: Vector | null): void;
        /** @deprecated This method will be removed in a future update */
        GetLocalVelcoity(): Vector;
    }

    interface EntityDamage {
        /** The amount of damage being applied, before armor and hitgroup modifications */
        damage: number;
        /** The type or types of damage. */
        damageTypes?: CSDamageTypes;
        /** The flags configuring how to interpret the damage. */
        damageFlags?: CSDamageFlags;
        /** The entity applying the damage. For bullets this is the owner of the gun. For grenades this is the exploding projectile. */
        inflictor?: Entity;
        /** The entity credited with causing the damage. For bullets this is the shooter. For grenades this is the thrower. */
        attacker?: Entity;
        /** The weapon used. For grenades this will not be present because the weapon is often removed before the projectile explodes. */
        weapon?: CSWeaponBase;
    }

    export class BaseModelEntity extends Entity {
        SetModel(modelName: string): void;
        GetModelName(): string;
        SetModelScale(scale: number): void;
        GetModelScale(): number;
        SetColor(color: ColorArg): void;
        GetColor(): Color;
        Glow(color?: ColorArg): void;
        Unglow(): void;
        IsGlowing(): boolean;
    }


    /** @deprecated This enum will be removed in a future update */
    export enum CSDamageType { }
}
