"""Video2X 6.4.0을 흉내 내는 가짜 스크립트 (통합 테스트 전용). GPU 없이 ffmpeg로 크기만 키운다.

진짜에서 확인한 동작을 그대로 따라 한다:
  - 인자: -i -o -p -s -d -c --pix-fmt -e key=value(여러 번) --no-copy-streams, 모델 옵션
  - 진행 줄: "<ESC>[Kframe=37/184 (20.11%); fps=14.00; elapsed=00:00:05; remaining=00:00:10" 을 CR로 덮어 쓴다
  - 끝나면 "Output written to: <파일>" 을 찍는다
  - 입력에 B프레임이 있으면 끝 프레임 2장을 잃는다
  - realesrgan-plus 모델은 4배만 된다

환경 변수:
  NEKOVUP_MOCK_V2X_EXIT   끝난 뒤 낼 종료 코드 (진짜는 정상 완료 후에도 0xC0000005로 죽곤 한다)
  NEKOVUP_MOCK_V2X_FAIL   1이면 아무것도 만들지 않고 오류로 끝낸다
  NEKOVUP_MOCK_V2X_DROP   끝에서 잃어버릴 프레임 수 (프레임 수 검증 테스트)
"""
import argparse
import os
import subprocess
import sys


def main():
    p = argparse.ArgumentParser(add_help=False)
    p.add_argument('-i', '--input', required=True)
    p.add_argument('-o', '--output', required=True)
    p.add_argument('-p', '--processor', required=True)
    p.add_argument('-s', '--scaling-factor', type=int, required=True)
    p.add_argument('-d', '--device', type=int, default=0)
    p.add_argument('-c', '--codec', default='libx264')
    p.add_argument('--pix-fmt', default='yuv420p')
    p.add_argument('-e', '--extra-encoder-option', action='append', default=[])
    p.add_argument('--no-copy-streams', action='store_true')
    p.add_argument('--realesrgan-model', default='realesr-animevideov3')
    p.add_argument('--realcugan-model', default='models-se')
    args = p.parse_args()

    print('[2026-01-01 00:00:00] [info] Video2X version 6.4.0 (mock)', flush=True)
    if os.environ.get('NEKOVUP_MOCK_V2X_FAIL') == '1':
        print('[2026-01-01 00:00:00] [critical] Failed to create Vulkan device', flush=True)
        return 1
    if args.processor not in ('realesrgan', 'realcugan', 'libplacebo', 'rife'):
        print(f'[critical] unknown processor {args.processor}', flush=True)
        return 1
    if args.processor == 'realesrgan' and args.realesrgan_model != 'realesr-animevideov3' and args.scaling_factor != 4:
        print(f'[critical] {args.realesrgan_model} only supports 4x', flush=True)
        return 1
    if args.scaling_factor not in (2, 3, 4):
        print('[critical] unsupported scaling factor', flush=True)
        return 1

    probe = subprocess.run(
        ['ffprobe', '-v', 'error', '-count_packets', '-select_streams', 'v:0',
         '-show_entries', 'stream=nb_read_packets,has_b_frames', '-of', 'csv=p=0', args.input],
        capture_output=True, text=True, encoding='utf-8')
    fields = [f for f in probe.stdout.strip().split(',') if f]
    if probe.returncode != 0 or len(fields) < 2:
        print('[critical] Failed to open input file', flush=True)
        return 1
    has_b_frames, total = int(fields[0]), int(fields[1])

    keep = total - int(os.environ.get('NEKOVUP_MOCK_V2X_DROP', '0'))
    if has_b_frames > 0:
        keep -= 2  # 진짜 6.4.0의 동작: B프레임 입력은 끝 2장이 사라진다

    encoder = dict(option.split('=', 1) for option in args.extra_encoder_option)
    command = ['ffmpeg', '-hide_banner', '-nostdin', '-y', '-loglevel', 'error', '-i', args.input,
               '-map', '0:v:0', '-frames:v', str(keep),
               '-vf', f'scale=iw*{args.scaling_factor}:ih*{args.scaling_factor}:flags=neighbor',
               '-c:v', args.codec, '-pix_fmt', args.pix_fmt]
    for key, value in encoder.items():
        command += [f'-{key}', value]
    if args.no_copy_streams:
        command += ['-an', '-sn']
    command.append(args.output)

    esc = chr(27)
    for frame in (0, keep // 2, keep):
        sys.stdout.write(f'\r{esc}[Kframe={frame}/{total + 4} ({100.0 * frame / max(total, 1):.2f}%); fps=14.00; elapsed=00:00:01; remaining=00:00:01')
        sys.stdout.flush()
    result = subprocess.run(command)
    if result.returncode != 0:
        print('\n[critical] encoding failed', flush=True)
        return 1

    print(f'\n[2026-01-01 00:00:01] [info] Video processed successfully', flush=True)
    print('====== Video2X Processing summary ======', flush=True)
    print(f'Total frames processed: {keep}', flush=True)
    print(f'Output written to: {args.output}', flush=True)
    sys.stdout.flush()
    # os._exit: 파이썬 정리 과정 없이 바로 끝낸다 (큰 부호 없는 코드도 그대로 전달된다)
    code = int(os.environ.get('NEKOVUP_MOCK_V2X_EXIT', '0'))
    os._exit(code - (1 << 32) if code >= (1 << 31) else code)  # os._exit은 부호 있는 32비트 값을 받는다


if __name__ == '__main__':
    sys.exit(main())
