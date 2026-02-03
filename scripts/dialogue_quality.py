#!/usr/bin/env python3
"""Dialogue reconstruction quality checker.

Parses dialogue.csv and dialogue_tree.txt output files from the Fallout Xbox 360
memory dump analyzer. Reports stats and compares across multiple dump outputs.

Usage:
    python scripts/dialogue_quality.py TestOutput/test_early [TestOutput/test_middle ...]
"""

import argparse
import csv
import os
import re
import sys


def parse_dialogue_csv(path):
    """Parse dialogue.csv and return quality stats."""
    stats = {
        "total_dialogues": 0,
        "total_responses": 0,
        "has_editor_id": 0,
        "has_topic_formid": 0,
        "has_quest_formid": 0,
        "has_speaker_formid": 0,
        "has_prompt_text": 0,
        "has_any_response_text": 0,  # DIALOGUE rows that have at least one non-empty RESPONSE
        "response_rows_with_text": 0,
        "has_flags": 0,
        "is_goodbye": 0,
        "is_say_once": 0,
        "is_speech_challenge": 0,
        "has_link_to": 0,
        "has_add_topics": 0,
        "single_char_prompt": 0,
        "no_text_no_prompt": 0,
        "big_endian": 0,
        "little_endian": 0,
    }

    current_dialogue_formid = None
    current_has_response = False
    dialogues_with_responses = set()

    with open(path, "r", encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        header = next(reader)

        # Build column index map
        col = {name: i for i, name in enumerate(header)}

        for row in reader:
            if len(row) < 2:
                continue

            row_type = row[col["RowType"]].strip()

            if row_type == "DIALOGUE":
                # Finalize previous dialogue
                if current_dialogue_formid and current_has_response:
                    dialogues_with_responses.add(current_dialogue_formid)

                stats["total_dialogues"] += 1
                formid = row[col["FormID"]].strip()
                current_dialogue_formid = formid
                current_has_response = False

                if row[col["EditorID"]].strip():
                    stats["has_editor_id"] += 1
                if row[col["TopicFormID"]].strip():
                    stats["has_topic_formid"] += 1
                if row[col["QuestFormID"]].strip():
                    stats["has_quest_formid"] += 1
                if row[col["SpeakerFormID"]].strip():
                    stats["has_speaker_formid"] += 1

                prompt = row[col["PromptText"]].strip()
                if prompt:
                    stats["has_prompt_text"] += 1
                    if len(prompt) == 1:
                        stats["single_char_prompt"] += 1

                flags_desc = row[col["FlagsDescription"]].strip()
                if flags_desc:
                    stats["has_flags"] += 1
                    if "Goodbye" in flags_desc:
                        stats["is_goodbye"] += 1
                    if "SayOnce" in flags_desc:
                        stats["is_say_once"] += 1
                    if "SpeechChallenge" in flags_desc:
                        stats["is_speech_challenge"] += 1

                if row[col["LinkToTopics"]].strip():
                    stats["has_link_to"] += 1
                if row[col["AddTopics"]].strip():
                    stats["has_add_topics"] += 1

                endian = row[col["Endianness"]].strip()
                if endian == "BE":
                    stats["big_endian"] += 1
                elif endian == "LE":
                    stats["little_endian"] += 1

            elif row_type == "RESPONSE":
                stats["total_responses"] += 1
                text = row[col["ResponseText"]].strip() if col["ResponseText"] < len(row) else ""
                if text:
                    stats["response_rows_with_text"] += 1
                    current_has_response = True

        # Finalize last dialogue
        if current_dialogue_formid and current_has_response:
            dialogues_with_responses.add(current_dialogue_formid)

    stats["has_any_response_text"] = len(dialogues_with_responses)

    # Compute "no text and no prompt" count
    # Re-read to count dialogues with neither response text nor prompt
    no_text_no_prompt = stats["total_dialogues"] - stats["has_prompt_text"]
    no_text_no_prompt -= stats["has_any_response_text"]
    # Some may have both; use min bound
    stats["no_text_no_prompt"] = max(0, stats["total_dialogues"] - stats["has_prompt_text"] - stats["has_any_response_text"])

    return stats


def parse_dialogue_tree(path):
    """Parse dialogue_tree.txt header stats."""
    stats = {
        "quests": 0,
        "topics": 0,
        "responses": 0,
        "orphan_topics": 0,
        "no_text_recovered": 0,
    }

    if not os.path.exists(path):
        return stats

    in_orphan = False
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip()

            m = re.match(r"\s*Quests:\s+([\d,]+)", line)
            if m:
                stats["quests"] = int(m.group(1).replace(",", ""))
                continue

            m = re.match(r"\s*Topics:\s+([\d,]+)", line)
            if m:
                stats["topics"] = int(m.group(1).replace(",", ""))
                continue

            m = re.match(r"\s*Responses:\s+([\d,]+)", line)
            if m:
                stats["responses"] = int(m.group(1).replace(",", ""))
                continue

            if "Orphan Topics" in line:
                in_orphan = True

            if in_orphan and line.strip().startswith("|-- Topic:"):
                stats["orphan_topics"] += 1

            if "(no text recovered)" in line:
                stats["no_text_recovered"] += 1

    return stats


def pct(num, denom):
    """Format percentage."""
    if denom == 0:
        return "N/A"
    return f"{num * 100 / denom:.1f}%"


def print_single_report(name, csv_stats, tree_stats):
    """Print report for a single dump."""
    t = csv_stats["total_dialogues"]
    r = csv_stats["total_responses"]

    print(f"\n{'=' * 70}")
    print(f"  {name}")
    print(f"{'=' * 70}")
    print()
    print(f"  CSV Summary:")
    print(f"    Total DIALOGUE rows:    {t:>8,}")
    print(f"    Total RESPONSE rows:    {r:>8,}")
    print(f"    Source: BE {csv_stats['big_endian']:,} / LE {csv_stats['little_endian']:,}")
    print()
    print(f"  Linking Quality:")
    print(f"    TopicFormID populated:  {csv_stats['has_topic_formid']:>8,}  ({pct(csv_stats['has_topic_formid'], t):>6})")
    print(f"    QuestFormID populated:  {csv_stats['has_quest_formid']:>8,}  ({pct(csv_stats['has_quest_formid'], t):>6})")
    print(f"    SpeakerFormID populated:{csv_stats['has_speaker_formid']:>8,}  ({pct(csv_stats['has_speaker_formid'], t):>6})")
    print(f"    EditorID populated:     {csv_stats['has_editor_id']:>8,}  ({pct(csv_stats['has_editor_id'], t):>6})")
    print()
    print(f"  Text Quality:")
    print(f"    Has PromptText:         {csv_stats['has_prompt_text']:>8,}  ({pct(csv_stats['has_prompt_text'], t):>6})")
    print(f"    Has ResponseText:       {csv_stats['has_any_response_text']:>8,}  ({pct(csv_stats['has_any_response_text'], t):>6})")
    print(f"    Response rows w/ text:  {csv_stats['response_rows_with_text']:>8,}  ({pct(csv_stats['response_rows_with_text'], r):>6})")
    print(f"    Single-char prompts:    {csv_stats['single_char_prompt']:>8,}  ({pct(csv_stats['single_char_prompt'], t):>6})")
    print(f"    No text AND no prompt:  {csv_stats['no_text_no_prompt']:>8,}  ({pct(csv_stats['no_text_no_prompt'], t):>6})")
    print()
    print(f"  Flags:")
    print(f"    Goodbye:                {csv_stats['is_goodbye']:>8,}  ({pct(csv_stats['is_goodbye'], t):>6})")
    print(f"    SayOnce:                {csv_stats['is_say_once']:>8,}  ({pct(csv_stats['is_say_once'], t):>6})")
    print(f"    SpeechChallenge:        {csv_stats['is_speech_challenge']:>8,}  ({pct(csv_stats['is_speech_challenge'], t):>6})")
    print(f"    Has LinkToTopics:       {csv_stats['has_link_to']:>8,}  ({pct(csv_stats['has_link_to'], t):>6})")
    print(f"    Has AddTopics:          {csv_stats['has_add_topics']:>8,}  ({pct(csv_stats['has_add_topics'], t):>6})")
    print()
    print(f"  Tree Summary:")
    print(f"    Quest trees:            {tree_stats['quests']:>8,}")
    print(f"    Total topics:           {tree_stats['topics']:>8,}")
    print(f"    Total responses:        {tree_stats['responses']:>8,}")
    print(f"    Orphan topics:          {tree_stats['orphan_topics']:>8,}")
    print(f"    '(no text recovered)':  {tree_stats['no_text_recovered']:>8,}")


def print_comparison_table(results):
    """Print side-by-side comparison table."""
    names = [r[0] for r in results]
    csv_stats_list = [r[1] for r in results]
    tree_stats_list = [r[2] for r in results]

    # Column width
    label_w = 26
    col_w = max(16, max(len(n) for n in names) + 2)

    print(f"\n{'=' * (label_w + col_w * len(names) + 4)}")
    print("  COMPARISON TABLE")
    print(f"{'=' * (label_w + col_w * len(names) + 4)}")
    print()

    # Header row
    header = f"  {'Metric':<{label_w}}"
    for name in names:
        header += f"{name:>{col_w}}"
    print(header)
    print(f"  {'-' * label_w}" + ("-" * col_w) * len(names))

    def row(label, getter):
        line = f"  {label:<{label_w}}"
        for cs in csv_stats_list:
            val = getter(cs)
            line += f"{val:>{col_w}}"
        print(line)

    def pct_row(label, key, denom_key="total_dialogues"):
        line = f"  {label:<{label_w}}"
        for cs in csv_stats_list:
            d = cs[denom_key]
            v = cs[key]
            line += f"{pct(v, d):>{col_w}}"
        print(line)

    def tree_row(label, key):
        line = f"  {label:<{label_w}}"
        for ts in tree_stats_list:
            line += f"{ts[key]:>{col_w},}"
        print(line)

    row("Dialogues", lambda cs: f"{cs['total_dialogues']:,}")
    row("Responses (CSV)", lambda cs: f"{cs['total_responses']:,}")
    row("Source BE/LE", lambda cs: f"{cs['big_endian']:,}/{cs['little_endian']:,}")
    print()
    pct_row("TopicFormID %", "has_topic_formid")
    pct_row("QuestFormID %", "has_quest_formid")
    pct_row("SpeakerFormID %", "has_speaker_formid")
    pct_row("EditorID %", "has_editor_id")
    print()
    pct_row("PromptText %", "has_prompt_text")
    pct_row("ResponseText %", "has_any_response_text")
    pct_row("Single-char prompt %", "single_char_prompt")
    pct_row("No text at all %", "no_text_no_prompt")
    print()
    pct_row("Goodbye %", "is_goodbye")
    pct_row("SayOnce %", "is_say_once")
    pct_row("SpeechChallenge %", "is_speech_challenge")
    print()
    tree_row("Quest trees", "quests")
    tree_row("Topics", "topics")
    tree_row("Orphan topics", "orphan_topics")
    tree_row("(no text recovered)", "no_text_recovered")


def main():
    parser = argparse.ArgumentParser(description="Dialogue reconstruction quality checker")
    parser.add_argument("dirs", nargs="+", help="Output directories to analyze (e.g., TestOutput/test_early)")
    args = parser.parse_args()

    results = []
    for d in args.dirs:
        name = os.path.basename(d.rstrip("/\\"))
        csv_path = os.path.join(d, "dialogue.csv")
        tree_path = os.path.join(d, "dialogue_tree.txt")

        if not os.path.exists(csv_path):
            print(f"WARNING: {csv_path} not found, skipping {name}", file=sys.stderr)
            continue

        csv_stats = parse_dialogue_csv(csv_path)
        tree_stats = parse_dialogue_tree(tree_path)
        results.append((name, csv_stats, tree_stats))

        print_single_report(name, csv_stats, tree_stats)

    if len(results) > 1:
        print_comparison_table(results)


if __name__ == "__main__":
    main()
