"""Exports the pretrained listening models the harness tries (plan T9.4k4, docs/adr/0104-pretrained-embeddings.md) to ONNX.

Each model becomes one ONNX file that takes plain audio, so the harness needs no Python and no feature code of its own:

* embedding models (panns, vggish, clap): input ``audio`` float32 [1, N], mono, -1..+1, at the model's rate;
  output ``embedding`` float32 [1, D];
* the distance model (cdpam): inputs ``reference`` and ``test``, both float32 [1, N]; output ``distance`` float32 [1].

N is the model's chunk (MODELS below; the harness's Embeddings.cs holds the same table). Next to each ``<name>.onnx`` the
script writes ``<name>.expected.txt``: the model's output for a fixed test signal, one number per line, which the
harness's tests compare with what ONNX Runtime gives in C#.

The weights are downloaded from their publishers on first use and never go in the repository. See README.md here.
"""

import argparse
import math
import os
import sys
from pathlib import Path

import numpy as np
import torch

# name: (rate in Hz, chunk in samples). Keep in step with EmbeddingModels in tools/Mod2Xgm.Harness/Embeddings.cs.
MODELS = {
    "panns": (32000, 64000),   # 2 s; CNN14 takes any length, 2 s keeps a short slip local
    "vggish": (16000, 15600),  # 0.975 s: exactly one 96-frame example
    "clap": (48000, 480000),   # 10 s: HTSAT's fixed input
    "cdpam": (22050, 44100),   # 2 s
}

OPSET = 17


def default_folder() -> Path:
    """The folder the harness looks in by default: .NET's LocalApplicationData + Mod2Xgm/models."""
    if sys.platform == "win32":
        base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    elif sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return base / "Mod2Xgm" / "models"


def check_signal(rate: int, samples: int, second: bool = False) -> np.ndarray:
    """The fixed test signal: 0.1 * sin(2 pi 440 t), plus 0.02 * sin(2 pi 3000 t) for cdpam's test input."""
    t = np.arange(samples, dtype=np.float64) / rate
    x = 0.1 * np.sin(2 * math.pi * 440 * t)
    if second:
        x = x + 0.02 * np.sin(2 * math.pi * 3000 * t)
    return x.astype(np.float32)[None, :]


# --- the models ---------------------------------------------------------------------------------------------------


class Panns(torch.nn.Module):
    """PANNs CNN14 (Kong et al. 2020) through panns_inference: the 2048-value embedding before the classifier."""

    def __init__(self):
        super().__init__()
        from panns_inference import AudioTagging

        self.model = AudioTagging(checkpoint_path=None, device="cpu").model.eval()

    def forward(self, audio):
        return self.model(audio)["embedding"]


class VggishFrontEnd(torch.nn.Module):
    """VGGish's log-mel examples (vggish_params / mel_features.py) as plain tensor ops, so ONNX needs no STFT op."""

    def __init__(self, mel_features):
        super().__init__()
        self.window, self.hop, self.fft = 400, 160, 512
        n = torch.arange(self.window, dtype=torch.float64)
        hann = 0.5 - 0.5 * torch.cos(2 * math.pi * n / self.window)  # periodic Hann, as mel_features.periodic_hann
        k = torch.arange(self.fft // 2 + 1, dtype=torch.float64)
        angle = 2 * math.pi * n[:, None] * k[None, :] / self.fft
        self.register_buffer("cos", (hann[:, None] * torch.cos(angle)).float())
        self.register_buffer("sin", (hann[:, None] * torch.sin(angle)).float())
        mel = mel_features.spectrogram_to_mel_matrix(
            num_mel_bins=64, num_spectrogram_bins=self.fft // 2 + 1, audio_sample_rate=16000,
            lower_edge_hertz=125.0, upper_edge_hertz=7500.0)
        self.register_buffer("mel", torch.from_numpy(mel).float())

    def forward(self, audio):
        frames = audio.unfold(1, self.window, self.hop)  # [1, 96, 400]
        re = frames @ self.cos
        im = frames @ self.sin
        magnitude = torch.sqrt(re * re + im * im)
        return torch.log(magnitude @ self.mel + 0.01).unsqueeze(1)  # [1, 1, 96, 64]


class Vggish(torch.nn.Module):
    """VGGish (Hershey et al. 2017) through torchvggish: the 128-value embedding before PCA."""

    def __init__(self):
        super().__init__()
        model = torch.hub.load("harritaylor/torchvggish", "vggish", postprocess=False, preprocess=False)
        self.model = model.eval()
        mel_features = sys.modules[type(model).__module__.replace("vggish", "mel_features")]
        self.front = VggishFrontEnd(mel_features)

    def forward(self, audio):
        x = self.model.features(self.front(audio))
        x = torch.transpose(x, 1, 3)
        x = torch.transpose(x, 1, 2)
        return self.model.embeddings(x.contiguous().view(x.size(0), -1))


class Clap(torch.nn.Module):
    """LAION-CLAP (Wu et al. 2023), HTSAT audio tower with the default checkpoint: the 512-value audio embedding."""

    def __init__(self):
        super().__init__()
        import laion_clap

        self.model = laion_clap.CLAP_Module(enable_fusion=False, device="cpu")
        self.model.load_ckpt()
        self.model.eval()

    def forward(self, audio):
        return self.model.get_audio_embedding_from_data(x=audio, use_tensor=True)


class Cdpam(torch.nn.Module):
    """CDPAM (Manocha et al. 2021): its learned distance between two clips. CDPAM reads audio scaled to 16-bit numbers."""

    def __init__(self):
        super().__init__()
        import cdpam

        self.loss = cdpam.CDPAM(dev="cpu")
        self.loss.model.eval()

    def forward(self, reference, test):
        return self.loss.forward(reference * 32768.0, test * 32768.0).reshape(1)


FACTORIES = {"panns": Panns, "vggish": Vggish, "clap": Clap, "cdpam": Cdpam}


# --- export -------------------------------------------------------------------------------------------------------


def export(name: str, folder: Path) -> None:
    import onnxruntime

    rate, samples = MODELS[name]
    model = FACTORIES[name]().eval()
    path = folder / f"{name}.onnx"
    with torch.no_grad():
        if name == "cdpam":
            inputs = (torch.from_numpy(check_signal(rate, samples)), torch.from_numpy(check_signal(rate, samples, True)))
            names, output = ["reference", "test"], "distance"
        else:
            inputs = (torch.from_numpy(check_signal(rate, samples)),)
            names, output = ["audio"], "embedding"
        expected = model(*inputs).detach().numpy().reshape(-1)
        torch.onnx.export(model, inputs, str(path), input_names=names, output_names=[output], opset_version=OPSET,
                          dynamo=False)

    session = onnxruntime.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    got = session.run([output], {n: x.numpy() for n, x in zip(names, inputs)})[0].reshape(-1)
    error = float(np.max(np.abs(got - expected)) / max(1e-6, float(np.max(np.abs(expected)))))
    if error > 1e-3:
        raise RuntimeError(f"ONNX output differs from PyTorch's by {error:.2e} of its largest value")
    (folder / f"{name}.expected.txt").write_text("".join(f"{v!r}\n" for v in got.astype(np.float64)), encoding="ascii")
    print(f"{name}: {path} ({path.stat().st_size // 1_000_000} MB, {got.size} values, ONNX vs PyTorch {error:.1e})")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, default=default_folder(), help="where the .onnx files go (default: %(default)s)")
    parser.add_argument("models", nargs="*", help=f"which models: {', '.join(MODELS)} (default: all)")
    args = parser.parse_args()
    unknown = [m for m in args.models if m not in MODELS]
    if unknown:
        parser.error(f"unknown model {', '.join(unknown)}; choose from {', '.join(MODELS)}")
    args.out.mkdir(parents=True, exist_ok=True)
    failed = []
    for name in args.models or list(MODELS):
        try:
            export(name, args.out)
        except Exception as error:  # one model failing must not stop the others
            print(f"{name}: failed: {error}", file=sys.stderr)
            failed.append(name)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
