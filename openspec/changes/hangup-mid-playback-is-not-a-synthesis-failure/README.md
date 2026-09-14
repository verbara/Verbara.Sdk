# hangup-mid-playback-is-not-a-synthesis-failure

Stop VoiceAiPipeline booking the far end's hangup during playback as a TTS failure: a write that finds
the audio session already gone is an ending, not a fault
