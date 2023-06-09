using System.Collections;
using System.Runtime.CompilerServices;
using UnityEngine;
using Unity.Burst;
using Unity.Mathematics;

/// <summary>
/// This script combines the functionality from <see cref="TabController"/> and <see cref="AdventurerTabs"/>
/// </summary>
public class NoticeBoard : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] private GridRouteInput _gridRoute;
    [SerializeField] private AdventurerSprites _adventurerSprites;

    [Header("Prefab References")]
    [SerializeField] private Tab[] _tabs;
    [SerializeField] private RectTransform[] _onScreenSpawnPositions;
    [SerializeField] private RectTransform[] _offScreenSpawnPositions;

    [Header("SFX References")]
    [SerializeField] private AudioClip _requestAdventurer;
    [SerializeField] private AudioClip _removeAdventurer;

    [Header("Animation Settings")]
    [SerializeField] private float _lerpOnScreenDuration = 1f;
    [SerializeField] private float _lerpFinishedWithTabDuration = 0.5f;

    [Header("Notice Board Wait Settings")]
    [SerializeField] private float _initialWaitTime = 0.5f;
    [SerializeField] private float _waitBetweenNextTab = 1f;


    private int _currentTabIndexToUseForSelection = 0;
    private int _currentTabIndexToMove = 0;
    private int _currentTabCountInBoard = 0;
    private int _MaxTabCount => _tabs.Length; 

    // function pointer delegates
    private delegate IEnumerator LerpDel(Tab tab, float3 start, float3 end, float duration);
    private delegate IEnumerator TrySendDel(float delay);
    private delegate IEnumerator EmptyDel();
    private TrySendDel WaitAndTrySendNewTabFunc;
    private LerpDel MoveTabOnScreenFunc;
    private LerpDel RemoveTabOffScreenFunc;
    private EmptyDel WaitAndAutoSelectAdventurerTabFunc;
    

    private void Awake()
    {
        Debug.Assert(_gridRoute         != null, "Grid Route Input is null, set in the inspector",   this);
        Debug.Assert(_adventurerSprites != null, "Adventurer Sprites is null, set in the inspector", this);

        // delegate allocations
        WaitAndTrySendNewTabFunc = WaitAndTrySendNewTab;
        MoveTabOnScreenFunc      = MoveTabOnScreen;
        RemoveTabOffScreenFunc   = RemoveTabOffScreen;
        WaitAndAutoSelectAdventurerTabFunc = WaitAndAutoSelectAdventurerTab;
    }

    private void Start()
    {
        for (int i = 0; i < _tabs.Length; ++i)
        {
            float3 startPosition = _offScreenSpawnPositions[i].position;
            _tabs[i].transform.position = startPosition;
        }


        // NOTE(Zack): we begin the infinite Coroutine to check if we're able to send a new tab onto the screen, at set intervals
        // This could easily be done in the [Update()] loop, but to simplify some of the code, this method has been chosen instead
        StartCoroutine(WaitAndTrySendNewTabFunc(_initialWaitTime)); 
        StartCoroutine(WaitAndAutoSelectAdventurerTab());
    }

    // HACK(Zack): this is a super scuffed way to do the automatically choose the next adventurer,
    // but it works and probably doesn't need to be expanded upon so it's probably fine for now.
    private IEnumerator WaitAndAutoSelectAdventurerTab()
    {
        while (true)
        {
            // we wait until we're able to read input and the tab is actually on screen
            Tab tab = _tabs[_currentTabIndexToUseForSelection]; 
            tab.StartFlashing(); // set the tab we're currently going to be using
            while (!tab.onScreen || _gridRoute._readingInput) yield return null;
            
            // start grid route selection input
            _gridRoute.StartRoute(tab.info);

            // we wait for the grid input to be finished
            while (_gridRoute._readingInput) yield return null;

            // remove the tab from the screen
            tab.StopFlashing();
            float3 startPos = tab.transform.position;
            float3 endPos   = startPos;
            endPos.x -= 2000f; // HACK(Zack): hardcorded for now;

            StartCoroutine(RemoveTabOffScreenFunc(tab, startPos, endPos, _lerpFinishedWithTabDuration));

            _currentTabIndexToUseForSelection += 1;
            _currentTabIndexToUseForSelection  = WrapIndex(_currentTabIndexToUseForSelection, _tabs.Length);

            _currentTabCountInBoard -= 1;
            _currentTabCountInBoard  = math.max(_currentTabCountInBoard, 0); // REVIEW(Zack): this is probably unnecessary, but just to be safe
            yield return null; // NOTE(Zack): unnecessary but putting this here for safety so that we don't get a spin lock
        }

        yield break;
    }

    [BurstCompile]
    private IEnumerator WaitAndTrySendNewTab(float delay)
    {
        // we wait before we check if we can send a new tab, so that we allow animations etc to finish
        float timer = 0f;
        while (timer < delay)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        // if we can send a new tab we lerp it on screen into the correct position
        if (_currentTabCountInBoard < _MaxTabCount) 
        {
            _currentTabCountInBoard += 1;

            // set the tabs info
            Character info = Adventurers.GetNextCharacter();
            Sprite sprite  = _adventurerSprites.GetSprite(info);
            Tab freeTab    = _tabs[_currentTabIndexToMove];
            freeTab.SetTabInfo(info, sprite);

            float3 startPosition = _offScreenSpawnPositions[_currentTabIndexToMove].position;
            float3 endPosition = _onScreenSpawnPositions[_currentTabIndexToMove].position;

            StartCoroutine(MoveTabOnScreenFunc(freeTab, startPosition, endPosition, _lerpOnScreenDuration));

            // we wrap the current tab index
            _currentTabIndexToMove += 1;
            _currentTabIndexToMove  = WrapIndex(_currentTabIndexToMove, _tabs.Length);
        }


        // we 'recurse' and call the same function again to begin the waiting and check again.
        // (this isn't a real recursion as we are not using the same Stack frame because of how Coroutines work)
        StartCoroutine(WaitAndTrySendNewTabFunc(_waitBetweenNextTab));
        yield break;
    }
    
    [BurstCompile]
    private IEnumerator MoveTabOnScreen(Tab tab, float3 startPos, float3 endPos, float duration)
    {
        AudioManager.PlayOneShot(_requestAdventurer);

        tab.transform.position = startPos;
        float timer = 0f;
        while (timer < duration)
        {
            float t = timer / duration;
            t = EaseOutBack(t);
            tab.transform.position = math.lerp(startPos, endPos, t);
            timer += Time.deltaTime;
            yield return null;
        }
        
        tab.transform.position = endPos;

        // NOTE(Zack): after the animation has finished this bool is set so that we are able,
        // to choose the character info from this tab for the character in the [Grid]
        tab.onScreen = true;
        yield break;
    }

    [BurstCompile]
    private IEnumerator RemoveTabOffScreen(Tab tab, float3 startPos, float3 endPos, float duration)
    {
        AudioManager.PlayOneShot(_removeAdventurer);

        // NOTE(Zack): this is set so that when an input is pressed we don't accidentally choose this tab
        // whilst it is animating off of the screen
        tab.onScreen = false;

        float timer = 0f;
        tab.transform.position = startPos;
        while (timer < duration)
        {
            float t = timer/ duration;
            t = EaseInSinusoidal(t);
            tab.transform.position = math.lerp(startPos, endPos, t);
            timer += Time.deltaTime;
            yield return null;
        }

        tab.transform.position = endPos;
        yield break;
    }

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EaseOutBack(float val) {
        const float s = 1.70158f;
        const float c3 = s + 1f;
        return 1f + c3 * math.pow(val - 1f, 3f) + s * math.pow(val - 1f, 2f);
    }

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EaseInSinusoidal(float val) => 1f - math.cos(val * (math.PI * 0.5f));

    [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WrapIndex(int index, int size) => (index + size) % size;
}
