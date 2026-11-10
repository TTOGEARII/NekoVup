# RealBasicVSR 추론 (옛 비디오·캠코더 영상용 4배 업스케일). NekoVup이 ProPainter용 파이썬 환경(torch)으로 돌린다.
#
# mmedit의 RealBasicVSRNet(= 사전 정리 모듈 + BasicVSR)을 순수 PyTorch로 다시 썼다 — mmcv/mmedit 설치 없이 공식 체크포인트
# (realbasicvsr_c64b20_1x30x8_lr5e-5_150k_reds)를 그대로 읽는다. 구조·연산은 mmedit 0.x의 real_basicvsr_net.py, basicvsr_net.py,
# flow_warp를 따른다. 체크포인트에 generator_ema가 있으면 그것을 쓴다(GAN 학습의 EMA 가중치가 보통 더 깨끗하다).
#
# 입출력은 ffmpeg 파이프: 입력 영상을 rgb24로 받아 4배 결과를 libx264로 쓴다. 진행은 "frame=i/N" 줄로 알린다.
#   python realbasicvsr.py --ffmpeg ffmpeg.exe --weights model.pth --input in.mkv --output out.mkv
#                          --width 720 --height 480 --frames 1500 --fps 30000/1001 --crf 12 --preset veryfast [--chunk 24]
import argparse
import os
import subprocess
import sys
import time

import torch
import torch.nn as nn
import torch.nn.functional as F


# ───────────────────────── 모델 ─────────────────────────

def flow_warp(x, flow, interpolation="bilinear", padding_mode="zeros", align_corners=True):
    """x: (n, c, h, w), flow: (n, h, w, 2) 픽셀 단위 변위."""
    n, _, h, w = x.size()
    grid_y, grid_x = torch.meshgrid(torch.arange(0, h, device=x.device), torch.arange(0, w, device=x.device), indexing="ij")
    grid = torch.stack((grid_x, grid_y), 2).type_as(x)
    grid_flow = grid + flow
    gx = 2.0 * grid_flow[:, :, :, 0] / max(w - 1, 1) - 1.0
    gy = 2.0 * grid_flow[:, :, :, 1] / max(h - 1, 1) - 1.0
    grid_flow = torch.stack((gx, gy), dim=3)
    return F.grid_sample(x, grid_flow, mode=interpolation, padding_mode=padding_mode, align_corners=align_corners)


class ResidualBlockNoBN(nn.Module):
    def __init__(self, mid_channels=64):
        super().__init__()
        self.conv1 = nn.Conv2d(mid_channels, mid_channels, 3, 1, 1, bias=True)
        self.conv2 = nn.Conv2d(mid_channels, mid_channels, 3, 1, 1, bias=True)
        self.relu = nn.ReLU(inplace=True)

    def forward(self, x):
        return x + self.conv2(self.relu(self.conv1(x)))


class ResidualBlocksWithInputConv(nn.Module):
    def __init__(self, in_channels, out_channels=64, num_blocks=30):
        super().__init__()
        self.main = nn.Sequential(
            nn.Conv2d(in_channels, out_channels, 3, 1, 1, bias=True),
            nn.LeakyReLU(negative_slope=0.1, inplace=True),
            nn.Sequential(*[ResidualBlockNoBN(out_channels) for _ in range(num_blocks)]),
        )

    def forward(self, x):
        return self.main(x)


class PixelShufflePack(nn.Module):
    def __init__(self, in_channels, out_channels, scale_factor, upsample_kernel):
        super().__init__()
        self.scale_factor = scale_factor
        self.upsample_conv = nn.Conv2d(in_channels, out_channels * scale_factor * scale_factor, upsample_kernel,
                                       padding=(upsample_kernel - 1) // 2)

    def forward(self, x):
        return F.pixel_shuffle(self.upsample_conv(x), self.scale_factor)


class ConvModule(nn.Module):
    """mmcv ConvModule(conv + act)의 가중치 이름(.conv)을 맞추기 위한 껍데기."""

    def __init__(self, in_channels, out_channels, kernel_size, padding, act=True):
        super().__init__()
        self.conv = nn.Conv2d(in_channels, out_channels, kernel_size, 1, padding)
        self.act = nn.ReLU(inplace=True) if act else None

    def forward(self, x):
        x = self.conv(x)
        return self.act(x) if self.act is not None else x


class SPyNetBasicModule(nn.Module):
    def __init__(self):
        super().__init__()
        self.basic_module = nn.Sequential(
            ConvModule(8, 32, 7, 3), ConvModule(32, 64, 7, 3), ConvModule(64, 32, 7, 3),
            ConvModule(32, 16, 7, 3), ConvModule(16, 2, 7, 3, act=False))

    def forward(self, x):
        return self.basic_module(x)


class SPyNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.basic_module = nn.ModuleList([SPyNetBasicModule() for _ in range(6)])
        self.register_buffer("mean", torch.tensor([0.485, 0.456, 0.406]).view(1, 3, 1, 1))
        self.register_buffer("std", torch.tensor([0.229, 0.224, 0.225]).view(1, 3, 1, 1))

    def compute_flow(self, ref, supp):
        n, _, h, w = ref.size()
        ref = [(ref - self.mean) / self.std]
        supp = [(supp - self.mean) / self.std]
        for _ in range(5):
            ref.append(F.avg_pool2d(ref[-1], kernel_size=2, stride=2, count_include_pad=False))
            supp.append(F.avg_pool2d(supp[-1], kernel_size=2, stride=2, count_include_pad=False))
        ref = ref[::-1]
        supp = supp[::-1]
        flow = ref[0].new_zeros(n, 2, h // 32, w // 32)
        for level in range(len(ref)):
            if level == 0:
                flow_up = flow
            else:
                flow_up = F.interpolate(flow, scale_factor=2, mode="bilinear", align_corners=True) * 2.0
            warped = flow_warp(supp[level], flow_up.permute(0, 2, 3, 1), padding_mode="border")
            flow = flow_up + self.basic_module[level](torch.cat([ref[level], warped, flow_up], 1))
        return flow

    def forward(self, ref, supp):
        h, w = ref.shape[2:4]
        w_up = w if (w % 32) == 0 else 32 * (w // 32 + 1)
        h_up = h if (h % 32) == 0 else 32 * (h // 32 + 1)
        ref = F.interpolate(ref, size=(h_up, w_up), mode="bilinear", align_corners=False)
        supp = F.interpolate(supp, size=(h_up, w_up), mode="bilinear", align_corners=False)
        flow = F.interpolate(self.compute_flow(ref, supp), size=(h, w), mode="bilinear", align_corners=False)
        flow[:, 0, :, :] *= float(w) / float(w_up)
        flow[:, 1, :, :] *= float(h) / float(h_up)
        return flow


class BasicVSRNet(nn.Module):
    def __init__(self, mid_channels=64, num_blocks=20):
        super().__init__()
        self.spynet = SPyNet()
        self.backward_resblocks = ResidualBlocksWithInputConv(mid_channels + 3, mid_channels, num_blocks)
        self.forward_resblocks = ResidualBlocksWithInputConv(mid_channels + 3, mid_channels, num_blocks)
        self.fusion = nn.Conv2d(mid_channels * 2, mid_channels, 1, 1, 0, bias=True)
        self.upsample1 = PixelShufflePack(mid_channels, mid_channels, 2, upsample_kernel=3)
        self.upsample2 = PixelShufflePack(mid_channels, 64, 2, upsample_kernel=3)
        self.conv_hr = nn.Conv2d(64, 64, 3, 1, 1)
        self.conv_last = nn.Conv2d(64, 3, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(negative_slope=0.1, inplace=True)
        self.mid_channels = mid_channels

    def compute_flow(self, lrs):
        n, t, c, h, w = lrs.size()
        lrs_1 = lrs[:, :-1, :, :, :].reshape(-1, c, h, w)
        lrs_2 = lrs[:, 1:, :, :, :].reshape(-1, c, h, w)
        flows_backward = self.spynet(lrs_1, lrs_2).view(n, t - 1, 2, h, w)
        flows_forward = self.spynet(lrs_2, lrs_1).view(n, t - 1, 2, h, w)
        return flows_forward, flows_backward

    def forward(self, lrs):
        n, t, c, h, w = lrs.size()
        flows_forward, flows_backward = self.compute_flow(lrs)

        outputs = []
        feat_prop = lrs.new_zeros(n, self.mid_channels, h, w)
        for i in range(t - 1, -1, -1):
            if i < t - 1:
                feat_prop = flow_warp(feat_prop, flows_backward[:, i, :, :, :].permute(0, 2, 3, 1))
            feat_prop = torch.cat([lrs[:, i, :, :, :], feat_prop], dim=1)
            feat_prop = self.backward_resblocks(feat_prop)
            outputs.append(feat_prop)
        outputs = outputs[::-1]

        feat_prop = torch.zeros_like(feat_prop)
        for i in range(0, t):
            lr_curr = lrs[:, i, :, :, :]
            if i > 0:
                feat_prop = flow_warp(feat_prop, flows_forward[:, i - 1, :, :, :].permute(0, 2, 3, 1))
            feat_prop = torch.cat([lr_curr, feat_prop], dim=1)
            feat_prop = self.forward_resblocks(feat_prop)

            out = torch.cat([outputs[i], feat_prop], dim=1)
            out = self.lrelu(self.fusion(out))
            out = self.lrelu(self.upsample1(out))
            out = self.lrelu(self.upsample2(out))
            out = self.lrelu(self.conv_hr(out))
            out = self.conv_last(out)
            base = F.interpolate(lr_curr, scale_factor=4, mode="bilinear", align_corners=False)
            outputs[i] = out + base
        return torch.stack(outputs, dim=1)


class RealBasicVSRNet(nn.Module):
    def __init__(self, mid_channels=64, num_propagation_blocks=20, num_cleaning_blocks=20, dynamic_refine_thres=255):
        super().__init__()
        self.basicvsr = BasicVSRNet(mid_channels, num_propagation_blocks)
        self.image_cleaning = nn.Sequential(
            ResidualBlocksWithInputConv(3, mid_channels, num_cleaning_blocks),
            nn.Conv2d(mid_channels, 3, 3, 1, 1, bias=True),
        )
        self.dynamic_refine_thres = dynamic_refine_thres / 255.0

    def forward(self, lqs):
        n, t, c, h, w = lqs.size()
        for _ in range(0, 3):  # 정리 모듈을 최대 3번 (잔차가 작아지면 그만)
            lqs = lqs.view(-1, c, h, w)
            residues = self.image_cleaning(lqs)
            lqs = (lqs + residues).view(n, t, c, h, w)
            if torch.mean(torch.abs(residues)) < self.dynamic_refine_thres:
                break
        return self.basicvsr(lqs)


def load_model(path, device):
    ck = torch.load(path, map_location="cpu", weights_only=True)  # 텐서만 읽는다 (체크포인트의 코드는 실행하지 않는다)
    sd = ck.get("state_dict", ck)
    prefix = "generator_ema." if any(k.startswith("generator_ema.") for k in sd) else "generator."
    sd = {k[len(prefix):]: v for k, v in sd.items() if k.startswith(prefix)}
    if not sd:
        sd = {k: v for k, v in ck.get("state_dict", ck).items()}
    model = RealBasicVSRNet()
    model.load_state_dict(sd, strict=True)
    return model.to(device).eval()


# ───────────────────────── 영상 입출력 ─────────────────────────

def color_filter(direction, matrix, color_range, pix_fmt):
    """영상↔RGB 변환 필터. 색 공간(matrix)·범위(range)가 주어지면 양쪽 변환에 똑같이 명시한다.

    주어지지 않으면 양쪽 다 ffmpeg 기본값이라 왕복이 맞는다. 한쪽만 표기를 따르면(읽을 때는 파일 표기, 쓸 때는 기본값) 색이 틀어진다.
    """
    options = []
    if matrix:
        options.append(f"{direction}_color_matrix={matrix}")
    if color_range:
        options.append(f"{direction}_range={color_range}")
    return (f"scale={':'.join(options)}," if options else "") + f"format={pix_fmt}"


def open_decoder(ffmpeg, path, matrix=None, color_range=None):
    return subprocess.Popen(
        [ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-map", "0:v:0",
         "-vf", color_filter("in", matrix, color_range, "rgb24"),
         "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)


def open_encoder(ffmpeg, path, width, height, fps, crf, preset, matrix=None, color_range=None):
    return subprocess.Popen(
        [ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
         "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{width}x{height}", "-r", fps, "-i", "-",
         "-vf", color_filter("out", matrix, color_range, "yuv420p"),
         "-c:v", "libx264", "-crf", str(crf), "-preset", preset, "-pix_fmt", "yuv420p", "-bf", "0", path],
        stdin=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)


def read_exact(pipe, size):
    """파이프 read()는 요청보다 적게 돌려줄 수 있다 — 프레임 하나가 다 찰 때까지 모은다. 끝이면 None."""
    chunks = []
    remaining = size
    while remaining > 0:
        data = pipe.read(remaining)
        if not data:
            return None
        chunks.append(data)
        remaining -= len(data)
    return b"".join(chunks)


def read_frames(pipe, count, frame_bytes):
    frames = []
    for _ in range(count):
        data = read_exact(pipe, frame_bytes)
        if data is None:
            break
        frames.append(data)
    return frames


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ffmpeg", required=True)
    ap.add_argument("--weights", required=True)
    ap.add_argument("--input", required=True)
    ap.add_argument("--output", required=True)
    ap.add_argument("--width", type=int, required=True)
    ap.add_argument("--height", type=int, required=True)
    ap.add_argument("--frames", type=int, required=True)
    ap.add_argument("--fps", required=True)
    ap.add_argument("--crf", type=int, default=12)
    ap.add_argument("--preset", default="veryfast")
    ap.add_argument("--matrix", default=None, help="입력의 색 공간 (scale 필터 이름: bt709, smpte170m …). 없으면 ffmpeg 기본값")
    ap.add_argument("--range", dest="color_range", default=None, choices=["tv", "pc"], help="입력의 색 범위. 없으면 ffmpeg 기본값")
    ap.add_argument("--chunk", type=int, default=24, help="한 번에 넣는 프레임 수 (VRAM이 모자라면 절반씩 줄인다)")
    ap.add_argument("--overlap", type=int, default=3, help="조각 사이에 겹쳐 넣고 버리는 프레임 수 (이음새 방지)")
    ap.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    args = ap.parse_args()

    device = torch.device(args.device)
    if device.type != "cuda":
        print("warning: CUDA를 쓸 수 없어 CPU로 돌립니다 (매우 느립니다)", flush=True)
    torch.backends.cudnn.benchmark = True  # 같은 크기의 프레임이 계속 오므로 가장 빠른 커널을 골라 둔다
    model = load_model(args.weights, device)
    if device.type == "cuda" and os.environ.get("NEKOVUP_CHANNELS_LAST", "1") == "1":
        model = model.to(memory_format=torch.channels_last)
    w, h = args.width, args.height
    frame_bytes = w * h * 3
    scale = 4

    dec = open_decoder(args.ffmpeg, args.input, args.matrix, args.color_range)
    enc = open_encoder(args.ffmpeg, args.output, w * scale, h * scale, args.fps, args.crf, args.preset, args.matrix, args.color_range)
    enc_broken = False  # 인코더가 먼저 죽어 더 쓸 수 없게 됐는지
    done = 0
    chunk = max(4, args.chunk)
    carry = []  # 앞 조각의 끝 프레임(겹침용)
    started = time.time()
    use_amp = device.type == "cuda"

    def run(frames):
        x = torch.frombuffer(bytearray(b"".join(frames)), dtype=torch.uint8).view(1, len(frames), h, w, 3)
        x = x.to(device).permute(0, 1, 4, 2, 3).float().div_(255.0)
        with torch.no_grad(), torch.autocast(device_type="cuda", dtype=torch.float16, enabled=use_amp):
            y = model(x)
        return y[0].float().clamp_(0, 1).mul_(255.0).round_().to(torch.uint8).permute(0, 2, 3, 1).contiguous().cpu()

    pending = []  # 디코더에서 읽었지만 아직 처리하지 않은 프레임
    eof = False
    try:
        while True:
            if len(pending) < chunk and not eof:
                more = read_frames(dec.stdout, chunk - len(pending), frame_bytes)
                if len(more) < chunk - len(pending):
                    eof = True
                pending += more
            if not pending:
                break
            take = pending[:chunk]
            frames = carry + take
            try:
                y = run(frames)
            except RuntimeError as ex:
                # VRAM 부족은 보통 torch.cuda.OutOfMemoryError(RuntimeError의 한 갈래)로 오지만, cuDNN 쪽에서 나면 일반 RuntimeError에 문구만 담겨 온다.
                if not isinstance(ex, torch.cuda.OutOfMemoryError) and "out of memory" not in str(ex).lower():
                    raise
                torch.cuda.empty_cache()
                if chunk <= 4:
                    raise
                chunk = max(4, chunk // 2)
                print(f"warning: VRAM 부족, 조각을 {chunk}장으로 줄입니다", flush=True)
                continue  # 같은 프레임을 더 작은 조각으로 다시
            y = y[len(carry):]
            try:
                enc.stdin.write(y.numpy().tobytes())
            except OSError:
                # 인코더가 먼저 끝났다(디스크가 찼거나 출력 파일을 못 씀). 파이프 오류 대신 아래에서 인코더가 남긴 오류를 알린다.
                enc_broken = True
                break
            done += y.shape[0]
            print(f"frame={done}/{args.frames}", flush=True)
            pending = pending[len(take):]
            carry = frames[-args.overlap:] if args.overlap > 0 else []
    finally:
        try:
            enc.stdin.close()
        except OSError:
            pass
        dec.stdout.close()
        dec_err = dec.stderr.read().decode("utf-8", "replace")
        enc_err = enc.stderr.read().decode("utf-8", "replace")
        dec.wait()
        enc.wait()

    if enc_broken:
        # 이때는 디코더도 읽는 쪽이 닫혀 오류로 끝나므로, 진짜 원인인 인코더 쪽을 먼저 알린다.
        print("ffmpeg(encode) failed: " + (enc_err or "출력 파일을 쓰지 못했습니다"), file=sys.stderr)
        sys.exit(3)
    if dec.returncode != 0:
        print("ffmpeg(decode) failed: " + dec_err, file=sys.stderr)
        sys.exit(2)
    if enc.returncode != 0:
        print("ffmpeg(encode) failed: " + enc_err, file=sys.stderr)
        sys.exit(3)
    elapsed = time.time() - started
    print(f"done frames={done} seconds={elapsed:.1f}", flush=True)


if __name__ == "__main__":
    main()
