using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays configurable sound effects for poker game events.
/// Each event has a <see cref="SoundGroup"/> with multiple clips;
/// clips are chosen randomly or cycled sequentially per hand (inspector toggle).
/// Respects <see cref="SoundManager"/> volume/enabled state.
/// </summary>
public class PokerSoundEffects : MonoBehaviour
{
    public static PokerSoundEffects Instance { get; private set; }

    // ── Inspector ────────────────────────────────────────────────

    [Header("Player Actions")]
    [Tooltip("Minimum gap between consecutive player-action sounds when several arrive back-to-back.")]
    [SerializeField] private float actionSoundSpacing = 1f;
    [SerializeField] private SoundGroup foldSound;
    [SerializeField] private SoundGroup checkSound;
    [SerializeField] private SoundGroup callSound;
    [SerializeField] private SoundGroup raiseSound;
    [SerializeField] private SoundGroup allInSound;
    [SerializeField] private SoundGroup betSound;

    [Header("Blinds")]
    [SerializeField] private SoundGroup smallBlindSound;
    [SerializeField] private SoundGroup bigBlindSound;

    [Header("Table Events")]
    [SerializeField] private SoundGroup playerJoinSound;
    [SerializeField] private SoundGroup playerLeaveSound;
    [SerializeField] private SoundGroup newHandSound;
    [SerializeField] private SoundGroup dealCardsSound;
    [SerializeField] private SoundGroup communityCardSound;
    [SerializeField] private SoundGroup collectBetsSound;
    [SerializeField] private SoundGroup winPotSound;
    [SerializeField] private SoundGroup yourTurnSound;
    [SerializeField] private SoundGroup timeoutFoldSound;
    [SerializeField] private SoundGroup timeBankLowSound;

    // ── Runtime ──────────────────────────────────────────────────

    private AudioSource audioSource;
    private readonly Queue<SoundGroup> actionSoundQueue = new Queue<SoundGroup>();
    private Coroutine actionSoundQueueCoroutine;

    // ════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void OnEnable()
    {
        if (GameManager.Instance != null)
            Subscribe();
        else
            GameManager.OnInstanceReady += OnGameManagerReady;
    }

    void OnDisable()
    {
        GameManager.OnInstanceReady -= OnGameManagerReady;
        if (GameManager.Instance != null)
            Unsubscribe();
        StopActionSoundQueue();
    }

    void OnGameManagerReady()
    {
        GameManager.OnInstanceReady -= OnGameManagerReady;
        Subscribe();
    }

    void Subscribe()
    {
        var gm = GameManager.Instance;
        gm.OnPlayerActionReceived += HandlePlayerAction;
        gm.OnPlayerBuyIn += HandlePlayerBuyIn;
        gm.OnHandStarted += HandleHandStarted;
    }

    void Unsubscribe()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnPlayerActionReceived -= HandlePlayerAction;
        gm.OnPlayerBuyIn -= HandlePlayerBuyIn;
        gm.OnHandStarted -= HandleHandStarted;
    }

    // ════════════════════════════════════════════════════════════════
    //  Event Handlers
    // ════════════════════════════════════════════════════════════════

    void HandlePlayerAction(int slotIndex, string action, float amount, int phase)
    {
        if (action.StartsWith("All-In"))       { EnqueueActionSound(allInSound); return; }
        if (action.StartsWith("Fold"))         { EnqueueActionSound(action == "Fold" ? foldSound : timeoutFoldSound); return; }

        switch (action)
        {
            case "Check":           EnqueueActionSound(checkSound); break;
            case "Call":            EnqueueActionSound(callSound); break;
            case "Raise":           EnqueueActionSound(raiseSound); break;
            case "Small Blind":     EnqueueActionSound(smallBlindSound); break;
            case "Big Blind":       EnqueueActionSound(bigBlindSound); break;
        }
    }

    void HandlePlayerBuyIn(int slotIndex, string playerName, float amount)
    {
        Play(playerJoinSound);
    }

    void HandleHandStarted(int hand, int dealer, int sb, int bb, int[] slots, float[] stacks)
    {
        StopActionSoundQueue();
        ResetAllSequentialIndices();
        Play(newHandSound);
    }

    // ── Public triggers (call from other scripts / ClientRpcs) ──

    public void PlayDealCards()         => Play(dealCardsSound);
    public void PlayCommunityCard()     => Play(communityCardSound);
    public void PlayCollectBets()       => Play(collectBetsSound);
    public void PlayWinPot()            => Play(winPotSound);
    public void PlayYourTurn()          => Play(yourTurnSound);
    public void PlayPlayerLeave()       => Play(playerLeaveSound);
    public void PlayBet()               => Play(betSound);
    public void PlayTimeBankLow()       => Play(timeBankLowSound);

    // ════════════════════════════════════════════════════════════════
    //  Core playback
    // ════════════════════════════════════════════════════════════════

    void EnqueueActionSound(SoundGroup group)
    {
        if (group == null || group.clips == null || group.clips.Length == 0)
            return;

        var sm = SoundManager.Instance;
        if (sm != null && !sm.AudioEnabled)
            return;

        actionSoundQueue.Enqueue(group);
        if (actionSoundQueueCoroutine == null)
            actionSoundQueueCoroutine = StartCoroutine(ProcessActionSoundQueue());
    }

    IEnumerator ProcessActionSoundQueue()
    {
        while (actionSoundQueue.Count > 0)
        {
            Play(actionSoundQueue.Dequeue());

            if (actionSoundQueue.Count > 0 && actionSoundSpacing > 0f)
                yield return new WaitForSeconds(actionSoundSpacing);
        }

        actionSoundQueueCoroutine = null;
    }

    void StopActionSoundQueue()
    {
        if (actionSoundQueueCoroutine != null)
        {
            StopCoroutine(actionSoundQueueCoroutine);
            actionSoundQueueCoroutine = null;
        }

        actionSoundQueue.Clear();
    }

    void Play(SoundGroup group)
    {
        if (group == null || group.clips == null || group.clips.Length == 0)
            return;

        var sm = SoundManager.Instance;
        if (sm != null && !sm.AudioEnabled)
            return;

        AudioClip clip = group.NextClip();
        if (clip == null)
            return;

        float volume = group.volume;
        if (sm != null)
            volume *= sm.MasterVolume;

        audioSource.pitch = UnityEngine.Random.Range(0.95f, 1.05f);
        audioSource.PlayOneShot(clip, volume);
    }

    void ResetAllSequentialIndices()
    {
        ResetGroup(foldSound);
        ResetGroup(checkSound);
        ResetGroup(callSound);
        ResetGroup(raiseSound);
        ResetGroup(allInSound);
        ResetGroup(betSound);
        ResetGroup(smallBlindSound);
        ResetGroup(bigBlindSound);
        ResetGroup(playerJoinSound);
        ResetGroup(playerLeaveSound);
        ResetGroup(newHandSound);
        ResetGroup(dealCardsSound);
        ResetGroup(communityCardSound);
        ResetGroup(collectBetsSound);
        ResetGroup(winPotSound);
        ResetGroup(yourTurnSound);
        ResetGroup(timeoutFoldSound);
        ResetGroup(timeBankLowSound);
    }

    static void ResetGroup(SoundGroup g) { if (g != null) g.ResetSequence(); }

    // ════════════════════════════════════════════════════════════════
    //  SoundGroup
    // ════════════════════════════════════════════════════════════════

    [Serializable]
    public class SoundGroup
    {
        [Tooltip("Audio clips for this event. Multiple clips enable random or sequential selection.")]
        public AudioClip[] clips;

        [Range(0f, 1f)]
        public float volume = 1f;

        [Tooltip("When enabled, clips play in order (cycling each occurrence) " +
                 "instead of being chosen randomly. Sequence resets each hand.")]
        public bool playInSequence;

        [NonSerialized] private int sequentialIndex;

        public AudioClip NextClip()
        {
            if (clips == null || clips.Length == 0)
                return null;

            if (clips.Length == 1)
                return clips[0];

            if (playInSequence)
            {
                int idx = sequentialIndex % clips.Length;
                sequentialIndex++;
                return clips[idx];
            }

            return clips[UnityEngine.Random.Range(0, clips.Length)];
        }

        public void ResetSequence() => sequentialIndex = 0;
    }
}
