import argparse
import os
import re
import subprocess
import sys


class ReservationError(Exception):
    pass


def git(*args, input_text=None):
    return subprocess.run(
        ["git", *args], input=input_text, text=True, capture_output=True, check=True,
    ).stdout.strip()


def read_reservations(series):
    namespace = f"refs/scrapbook/reservations/{series}"
    git("fetch", "--prune", "--no-tags", "origin", f"refs/tags/v{series}.*:{namespace}/*")
    rows = git(
        "for-each-ref", "--format=%(refname)%00%(objecttype)%00%(*objectname)%00%(contents:subject)",
        namespace,
    )
    reservations = {}
    for row in rows.splitlines():
        ref, kind, sha, subject = row.split("\0")
        patch = ref.removeprefix(namespace + "/")
        if not re.fullmatch(r"0|[1-9][0-9]*", patch):
            continue
        if kind != "tag" or not re.fullmatch(r"[0-9a-f]{40}", sha):
            raise ReservationError(f"A versão v{series}.{patch} deve ter tag anotada.")
        reservations[int(patch)] = (sha, subject)
    return reservations


def reserve(series, sha, run_key):
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", series):
        raise ReservationError("Use a série MAJOR.MINOR em RELEASE_SERIES.")
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or git("rev-parse", "HEAD") != sha:
        raise ReservationError("O SHA informado deve coincidir com o checkout.")
    if not re.fullmatch(r"[1-9][0-9]*\.[1-9][0-9]*", run_key):
        raise ReservationError("Use o identificador e a tentativa da execução.")

    subject = f"ScrapBook release {run_key}"
    for _ in range(12):
        known = read_reservations(series)
        previous = [patch for patch, row in known.items() if row[1] == subject]
        if previous:
            if len(previous) != 1 or known[previous[0]][0] != sha:
                raise ReservationError("A execução já tem reserva incompatível com o checkout.")
            return f"v{series}.{previous[0]}"
        patch = max(known, default=-1) + 1
        tag = f"v{series}.{patch}"
        identity = git("var", "GIT_COMMITTER_IDENT")
        oid = git("mktag", input_text=f"object {sha}\ntype commit\ntag {tag}\ntagger {identity}\n\n{subject}\n")
        try:
            git("push", "--atomic", "origin", f"{oid}:refs/tags/{tag}")
            return tag
        except subprocess.CalledProcessError:
            updated = read_reservations(series)
            if patch not in updated:
                raise ReservationError(
                    "O remoto não confirmou a reserva. Confira conectividade e regras de criação das tags."
                ) from None
    raise ReservationError("Reserva concorrente excedeu o limite de tentativas.")


def main():
    parser = argparse.ArgumentParser(description="Reserva uma versão anotada no remoto origin.")
    parser.add_argument("series")
    parser.add_argument("sha")
    parser.add_argument("run_key")
    args = parser.parse_args()
    try:
        version = reserve(args.series, args.sha, args.run_key)
    except (ReservationError, OSError, subprocess.CalledProcessError) as error:
        message = str(error) if isinstance(error, ReservationError) else "Falha ao consultar o Git."
        print(message, file=sys.stderr)
        return 1
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as destination:
            destination.write(f"version={version}\nrun_key={args.run_key}\n")
    print(version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
