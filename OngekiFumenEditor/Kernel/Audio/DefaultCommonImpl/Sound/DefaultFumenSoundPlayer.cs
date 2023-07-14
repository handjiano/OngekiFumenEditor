﻿using Caliburn.Micro;
using FontStashSharp;
using IntervalTree;
using OngekiFumenEditor.Base;
using OngekiFumenEditor.Base.OngekiObjects;
using OngekiFumenEditor.Base.OngekiObjects.Beam;
using OngekiFumenEditor.Modules.FumenVisualEditor;
using OngekiFumenEditor.Modules.FumenVisualEditor.ViewModels;
using OngekiFumenEditor.Properties;
using OngekiFumenEditor.Utils;
using OngekiFumenEditor.Utils.ObjectPool;
using SharpVectors.Dom.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace OngekiFumenEditor.Kernel.Audio.DefaultCommonImpl.Sound
{
    [Export(typeof(IFumenSoundPlayer))]
    public partial class DefaultFumenSoundPlayer : PropertyChangedBase, IFumenSoundPlayer, IDisposable
    {
        private IntervalTree<TimeSpan, DurationSoundEvent> durationEvents = new();
        private HashSet<DurationSoundEvent> currentPlayingDurationEvents = new();

        private LinkedList<SoundEvent> events = new();
        private LinkedListNode<SoundEvent> itor;

        private AbortableThread thread;

        private IAudioPlayer player;
        private FumenVisualEditorViewModel editor;
        private bool isPlaying = false;
        public bool IsPlaying => isPlaying && (player?.IsPlaying ?? false);
        private static int loopIdGen = 0;

        public SoundControl SoundControl { get; set; } = SoundControl.All;

        private float volume = 1;
        public float Volume
        {
            get => volume;
            set
            {
                Set(ref volume, value);
            }
        }

        private Dictionary<SoundControl, ISoundPlayer> cacheSounds = new();
        private Task loadTask;

        public DefaultFumenSoundPlayer()
        {
            InitSounds();
        }

        private async void InitSounds()
        {
            var source = new TaskCompletionSource();
            loadTask = source.Task;
            var audioManager = IoC.Get<IAudioManager>();

            var soundFolderPath = AudioSetting.Default.SoundFolderPath;
            if (!Directory.Exists(soundFolderPath))
            {
                var msg = $"因为音效文件夹不存在,无法加载音效";
                MessageBox.Show(msg);
                Log.LogError(msg);
            }
            else
                Log.LogInfo($"SoundFolderPath : {soundFolderPath} , fullpath : {Path.GetFullPath(soundFolderPath)}");

            bool noError = true;

            async Task load(SoundControl sound, string fileName)
            {
                var fixFilePath = Path.Combine(soundFolderPath, fileName);

                try
                {
                    cacheSounds[sound] = await audioManager.LoadSoundAsync(fixFilePath);
                }
                catch (Exception e)
                {
                    Log.LogError($"Can't load {sound} sound file : {fixFilePath} , reason : {e.Message}");
                    noError = false;
                }
            }

            await load(SoundControl.Tap, "tap.wav");
            await load(SoundControl.Bell, "bell.wav");
            await load(SoundControl.CriticalTap, "extap.wav");
            await load(SoundControl.WallTap, "wall.wav");
            await load(SoundControl.CriticalWallTap, "exwall.wav");
            await load(SoundControl.Flick, "flick.wav");
            await load(SoundControl.Bullet, "bullet.wav");
            await load(SoundControl.CriticalFlick, "exflick.wav");
            await load(SoundControl.HoldEnd, "holdend.wav");
            await load(SoundControl.ClickSE, "clickse.wav");
            await load(SoundControl.HoldTick, "holdtick.wav");
            await load(SoundControl.BeamPrepare, "beamprepare.wav");
            await load(SoundControl.BeamLoop, "beamlooping.wav");
            await load(SoundControl.BeamEnd, "beamend.wav");

            if (!noError)
                MessageBox.Show("部分音效并未加载成功,详情可查看日志");

            source.SetResult();
        }

        public async Task Prepare(FumenVisualEditorViewModel editor, IAudioPlayer player)
        {
            await loadTask;

            if (thread is not null)
            {
                thread.Abort();
                thread = null;
            }

            this.player = player;
            this.editor = editor;

            RebuildEvents();

            thread = new AbortableThread(OnUpdate);
            thread.Name = $"DefaultFumenSoundPlayer_Thread";
            thread.Start();
        }

        private static IEnumerable<TGrid> CalculateHoldTicks(Hold x, OngekiFumen fumen)
        {
            int? CalcHoldTickStepSizeA()
            {
                //calculate stepGrid
                var met = fumen.MeterChanges.GetMeter(x.TGrid);
                var bpm = fumen.BpmList.GetBpm(x.TGrid);
                var resT = bpm.TGrid.ResT;
                var beatCount = met.BunShi * 1;
                if (beatCount == 0)
                    return null;
                return (int)(resT / beatCount);
            }
            /*
            int? CalcHoldTickStepSizeB()
            {
                var bpm = fumen.BpmList.GetBpm(x.TGrid).BPM;
                var progressJudgeBPM = fumen.MetaInfo.ProgJudgeBpm;
                var standardBeatLen = fumen.MetaInfo.TRESOLUTION >> 2; //取1/4切片长度

                if (bpm < progressJudgeBPM)
                {
                    while (bpm < progressJudgeBPM)
                    {
                        standardBeatLen >>= 1;
                        bpm *= 2f;
                    }
                }
                else
                {
                    for (progressJudgeBPM *= 2f; progressJudgeBPM <= bpm; progressJudgeBPM *= 2f)
                    {
                        standardBeatLen <<= 1;
                    }
                }
                return standardBeatLen;
            }
            */

            if (CalcHoldTickStepSizeA() is not int lengthPerBeat)
                yield break;
            var stepGrid = new GridOffset(0, lengthPerBeat);

            var curTGrid = x.TGrid + stepGrid;
            if (x.HoldEnd is null)
                yield break;
            while (curTGrid < x.HoldEnd.TGrid)
            {
                yield return curTGrid;
                curTGrid = curTGrid + stepGrid;
            }
        }

        private static IEnumerable<TGrid> CalculateDefaultClickSEs(OngekiFumen fumen)
        {
            var tGrid = TGrid.Zero;
            var endTGrid = new TGrid(1, 0);
            //calculate stepGrid
            var met = fumen.MeterChanges.GetMeter(tGrid);
            var bpm = fumen.BpmList.GetBpm(tGrid);
            var resT = bpm.TGrid.ResT;
            var beatCount = met.BunShi * 1;
            if (beatCount != 0)
            {
                var lengthPerBeat = (int)(resT / beatCount);

                var stepGrid = new GridOffset(0, lengthPerBeat);

                var curTGrid = tGrid + stepGrid;
                while (curTGrid < endTGrid)
                {
                    yield return curTGrid;
                    curTGrid = curTGrid + stepGrid;
                }
            }
        }

        private void RebuildEvents()
        {
            StopAllLoop();
            events.ForEach(ObjectPool<SoundEvent>.Return);
            durationEvents.Select(x => x.Value).ForEach(ObjectPool<DurationSoundEvent>.Return);
            events.Clear();
            durationEvents.Clear();
            currentPlayingDurationEvents.Clear();

            var list = new HashSet<SoundEvent>();
            var durationList = new HashSet<DurationSoundEvent>();

            void AddSound(SoundControl sound, TGrid tGrid)
            {
                var evt = ObjectPool<SoundEvent>.Get();

                evt.Sounds = sound;
                evt.Time = TGridCalculator.ConvertTGridToAudioTime(tGrid, editor);
                //evt.TGrid = tGrid;

                list.Add(evt);
            }

            void AddDurationSound(SoundControl sound, TGrid tGrid, TGrid endTGrid, int loopId = 0)
            {
                var evt = ObjectPool<DurationSoundEvent>.Get();

                evt.Sounds = sound;
                evt.LoopId = loopId;
                evt.Time = TGridCalculator.ConvertTGridToAudioTime(tGrid, editor);
                evt.EndTime = TGridCalculator.ConvertTGridToAudioTime(endTGrid, editor);
                //evt.TGrid = tGrid;

                durationList.Add(evt);
            }

            var fumen = editor.Fumen;

            var soundObjects = fumen.GetAllDisplayableObjects().OfType<OngekiTimelineObjectBase>();

            //add default clickse objects.
            foreach (var tGrid in CalculateDefaultClickSEs(fumen))
                AddSound(SoundControl.ClickSE, tGrid);

            foreach (var group in soundObjects.GroupBy(x => x.TGrid))
            {
                var sounds = (SoundControl)0;

                foreach (var obj in group.DistinctBy(x => x.GetType()))
                {
                    sounds = sounds | obj switch
                    {
                        WallTap { IsCritical: false } => SoundControl.WallTap,
                        WallTap { IsCritical: true } => SoundControl.CriticalWallTap,
                        Tap { IsCritical: false } or Hold { IsCritical: false } => SoundControl.Tap,
                        Tap { IsCritical: true } or Hold { IsCritical: true } => SoundControl.CriticalTap,
                        Bell => SoundControl.Bell,
                        Bullet => SoundControl.Bullet,
                        Flick { IsCritical: false } => SoundControl.Flick,
                        Flick { IsCritical: true } => SoundControl.CriticalFlick,
                        HoldEnd => SoundControl.HoldEnd,
                        ClickSE => SoundControl.ClickSE,
                        _ => default
                    };

                    if (obj is Hold hold)
                    {
                        //add hold ticks
                        foreach (var tickTGrid in CalculateHoldTicks(hold, fumen))
                        {
                            AddSound(SoundControl.HoldTick, tickTGrid);
                        }
                    }

                    if (obj is BeamStart beam)
                    {
                        var loopId = ++loopIdGen;

                        //generate stop
                        AddSound(SoundControl.BeamEnd, beam.MaxTGrid);
                        AddDurationSound(SoundControl.BeamLoop, beam.TGrid, beam.MaxTGrid, loopId);
                        var leadBodyInTGrid = TGridCalculator.ConvertAudioTimeToTGrid(TGridCalculator.ConvertTGridToAudioTime(beam.TGrid, editor) - TimeSpan.FromMilliseconds(BeamStart.LEAD_IN_DURATION), editor);
                        AddSound(SoundControl.BeamPrepare, leadBodyInTGrid);
                    }
                }
                if (sounds != 0)
                    AddSound(sounds, group.Key);
            }
            events = new LinkedList<SoundEvent>(list.OrderBy(x => x.Time));
            foreach (var durationEvent in durationList)
                durationEvents.Add(durationEvent.Time, durationEvent.EndTime, durationEvent);
            itor = events.First;
        }

        private void OnUpdate(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                if (itor is null || player is null)
                    continue;
                if (!IsPlaying)
                {
                    //stop all looping
                    StopAllLoop();
                    continue;
                }

                var currentTime = player.CurrentTime;

                //播放普通音乐
                while (itor is not null)
                {
                    var nextBeatTime = itor.Value.Time.TotalMilliseconds;
                    var ct = currentTime.TotalMilliseconds - nextBeatTime;
                    if (ct >= 0)
                    {
                        //Debug.WriteLine($"diff:{ct:F2}ms target:{itor.Value}");
                        ProcessSoundEvent(itor.Value);
                        itor = itor.Next;
                    }
                    else
                        break;
                }

                var queryDurationEvents = durationEvents.Query(currentTime);
                foreach (var durationEvent in queryDurationEvents)
                {
                    //检查是否正在播放了
                    if (!currentPlayingDurationEvents.Contains(durationEvent))
                    {
                        if (SoundControl.HasFlag(durationEvent.Sounds) && cacheSounds.TryGetValue(durationEvent.Sounds, out var soundPlayer))
                        {
                            var initPlayTime = currentTime - durationEvent.Time;
                            soundPlayer.PlayLoop(durationEvent.LoopId, initPlayTime);
                            currentPlayingDurationEvents.Add(durationEvent);
                        }
                    }
                }

                //检查是否已经播放完成
                foreach (var durationEvent in currentPlayingDurationEvents.Where(x => currentTime < x.Time || currentTime > x.EndTime).ToArray())
                {
                    if (cacheSounds.TryGetValue(durationEvent.Sounds, out var soundPlayer))
                    {
                        soundPlayer.StopLoop(durationEvent.LoopId);
                        currentPlayingDurationEvents.Remove(durationEvent);
                    }
                }

                /*
                else
                {
                    var sleepTime = Math.Min(1000, (int)((Math.Abs(ct) - 2) * player.Speed));
                    if (ct < -5 && sleepTime > 0)
                        Thread.Sleep(sleepTime);
                    break;
                }*/

            }
        }

        private void ProcessSoundEvent(SoundEvent evt)
        {
            var sounds = evt.Sounds;

            void checkPlay(SoundControl subFlag)
            {
                if (sounds.HasFlag(subFlag) && SoundControl.HasFlag(subFlag) && cacheSounds.TryGetValue(subFlag, out var sound))
                    sound.PlayOnce();
            }

            checkPlay(SoundControl.Tap);
            checkPlay(SoundControl.CriticalTap);
            checkPlay(SoundControl.Bell);
            checkPlay(SoundControl.WallTap);
            checkPlay(SoundControl.CriticalWallTap);
            checkPlay(SoundControl.Bullet);
            checkPlay(SoundControl.Flick);
            checkPlay(SoundControl.CriticalFlick);
            checkPlay(SoundControl.HoldEnd);
            checkPlay(SoundControl.HoldTick);
            checkPlay(SoundControl.ClickSE);
            checkPlay(SoundControl.BeamPrepare);
            checkPlay(SoundControl.BeamEnd);
        }

        public void Seek(TimeSpan msec, bool pause)
        {
            Pause();
            itor = events.Find(events.FirstOrDefault(x => msec < x.Time));

            if (!pause)
                Play();
        }

        private void StopAllLoop()
        {
            foreach (var durationEvent in currentPlayingDurationEvents.ToArray())
            {
                if (cacheSounds.TryGetValue(durationEvent.Sounds, out var soundPlayer))
                {
                    soundPlayer.StopLoop(durationEvent.LoopId);
                    currentPlayingDurationEvents.Remove(durationEvent);
                }
            }
        }

        public void Stop()
        {
            StopAllLoop();

            thread?.Abort();
            isPlaying = false;
        }

        public void Play()
        {
            if (player is null)
                return;
            isPlaying = true;
        }

        public void Pause()
        {
            isPlaying = false;
        }

        public void Dispose()
        {
            thread?.Abort();
            foreach (var sound in cacheSounds.Values)
                sound.Dispose();
        }

        public Task Clean()
        {
            Stop();

            thread = null;

            player = null;
            editor = null;

            events.Clear();

            return Task.CompletedTask;
        }

        public float GetVolume(SoundControl sound)
        {
            foreach (var item in cacheSounds)
            {
                if (item.Key == sound)
                {
                    return item.Value.Volume;
                }
            }

            return 0;
        }

        public void SetVolume(SoundControl sound, float volume)
        {
            foreach (var item in cacheSounds)
            {
                if (item.Key == sound)
                {
                    item.Value.Volume = volume;
                }
            }
        }
    }
}
