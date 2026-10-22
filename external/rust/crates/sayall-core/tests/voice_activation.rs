//! Characterises when the ATVV voice stream can start.
//!
//! The real hardware question is whether the remote only opens its microphone
//! after the dedicated voice key is pressed, or whether another button / a
//! host-side ATVV command can start it.  The platform tests cannot press a
//! physical key; these tests pin down the host-side gate (or lack of one):
//!
//! * only control opcode `0x08` asks the host to send `MIC_OPEN`;
//! * a remote-originated `0x04` is accepted as a stream start **without** any
//!   preceding `0x08`, so the host does not enforce "voice key first";
//! * `0x08` by itself does not start audio.
//!
//! Use `tools/RemoteMicActivationProbe` for the actual remote/button test.

use sayall_core::{AtvvVoicePipeline, PipelineOutput, VoiceSessionState};

/// RC003 capabilities: v1.0, ADPCM codec, 4-byte frames, 16 kHz.
const CAPS: [u8; 7] = [0x0B, 0x01, 0x00, 0x02, 0x03, 0, 4];

fn ready_pipeline() -> AtvvVoicePipeline {
    let mut pipeline = AtvvVoicePipeline::default();
    assert!(matches!(
        pipeline.handle_control(&CAPS).expect("capabilities"),
        PipelineOutput::Ready(_)
    ));
    pipeline
}

#[test]
fn only_opcode_0x08_requests_microphone_open() {
    let mut pipeline = ready_pipeline();
    assert_eq!(
        pipeline
            .handle_control(&[0x08])
            .expect("microphone-open request"),
        PipelineOutput::MicrophoneOpenRequested
    );

    // A stream start is not a microphone-open request, even though it starts
    // audio.  This keeps the two protocol intents separate.
    assert!(matches!(
        pipeline
            .handle_control(&[0x04, 0x03, 0x02, 0x01])
            .expect("stream start"),
        PipelineOutput::StreamStarted { .. }
    ));
}

#[test]
fn stream_started_without_open_request_is_accepted() {
    let mut pipeline = ready_pipeline();

    // No 0x08 was sent by the remote and no host MIC_OPEN happened.  The
    // current host still accepts the remote-originated stream.
    assert_eq!(
        pipeline
            .handle_control(&[0x04, 0x03, 0x02, 0x07])
            .expect("stream start without voice-key request"),
        PipelineOutput::StreamStarted {
            session_id: 7,
            generation: 1,
        }
    );
    assert_eq!(pipeline.state(), VoiceSessionState::Streaming);
    assert!(matches!(
        pipeline.handle_audio(&[0x11; 4]).expect("audio"),
        PipelineOutput::Samples { generation: 1, .. }
    ));
}

#[test]
fn microphone_open_request_alone_does_not_start_audio() {
    let mut pipeline = ready_pipeline();
    assert_eq!(
        pipeline
            .handle_control(&[0x08])
            .expect("microphone-open request"),
        PipelineOutput::MicrophoneOpenRequested
    );
    assert_eq!(pipeline.state(), VoiceSessionState::Idle);
    assert!(pipeline.handle_audio(&[0x11; 4]).is_err());
}
