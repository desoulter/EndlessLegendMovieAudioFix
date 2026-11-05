#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

/* Contract checks only: this library does not decode audio or open an audio device. */
static int stops, sounds_released, systems_created, systems_released;
static int fail_stream, finished;
static float last_volume;

int FMOD_System_Create(void **system)
{
    *system = malloc(1);
    systems_created++;
    return 0;
}

int FMOD_System_Init(void *system, int channels, unsigned flags, void *extra)
{
    (void)system;
    (void)channels;
    (void)flags;
    (void)extra;
    return 0;
}

int FMOD_System_CreateStream(void *system, const char *path, unsigned mode,
                            void *info, void **sound)
{
    (void)info;
    assert(system && mode == 9);
    if (fail_stream) return 99;
    FILE *input = fopen(path, "rb");
    assert(input);
    fclose(input);
    *sound = malloc(1);
    return 0;
}

int FMOD_System_PlaySound(void *system, int index, void *sound, int paused, void **channel)
{
    assert(system && sound && index == -1 && paused == 1);
    *channel = malloc(1);
    return 0;
}

int FMOD_Channel_SetVolume(void *channel, float volume)
{
    assert(channel);
    last_volume = volume;
    return 0;
}

int FMOD_Channel_SetPaused(void *channel, int paused)
{
    assert(channel && paused == 0);
    return 0;
}

int FMOD_Channel_Stop(void *channel)
{
    assert(channel);
    free(channel);
    stops++;
    return 0;
}

int FMOD_Channel_IsPlaying(void *channel, int *playing)
{
    assert(channel);
    *playing = !finished;
    return 0;
}

int FMOD_Sound_Release(void *sound)
{
    assert(sound);
    free(sound);
    sounds_released++;
    return 0;
}

int FMOD_System_Update(void *system)
{
    assert(system);
    return 0;
}

int FMOD_System_Release(void *system)
{
    assert(system && (uintptr_t)system != 0x1234);
    free(system);
    systems_released++;
    return 0;
}

int test_stops(void) { return stops; }
int test_releases(void) { return sounds_released; }
int test_systems(void) { return systems_created; }
int test_system_releases(void) { return systems_released; }
float test_volume(void) { return last_volume; }
void test_fail(int value) { fail_stream = value; }
void test_finished(int value) { finished = value; }
