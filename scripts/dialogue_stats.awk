BEGIN { FS=","; t=0; tp=0; qp=0 }
/^DIALOGUE,/ {
    t++
    if ($4 != "") tp++
    if ($6 != "") qp++
}
END {
    printf "Total: %d, TopicFormID: %d (%.1f%%), QuestFormID: %d (%.1f%%)\n", t, tp, tp*100/t, qp, qp*100/t
}
