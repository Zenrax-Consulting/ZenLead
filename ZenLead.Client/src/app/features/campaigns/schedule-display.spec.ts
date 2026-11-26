import { SendDays, daysToSet, parseSendDays, setToDays } from './campaigns.models';
import { buildTimeline, daysSummary, stepSendLabel, timelineSummary, windowSummary } from './schedule-display';

const steps = (...delays: number[]) => delays.map((delayDays, i) => ({ order: i + 1, delayDays }));

describe('schedule-display', () => {
  it('accumulates delays: [0, 3, 4] → Day 0, 3, 7', () => {
    expect(buildTimeline(steps(0, 3, 4)).map(t => t.day)).toEqual([0, 3, 7]);
    expect(timelineSummary(steps(0, 3, 4))).toBe('Day 0 → Day 3 → Day 7');
  });

  it('sorts unsorted input by order', () => {
    const unsorted = [{ order: 3, delayDays: 4 }, { order: 1, delayDays: 0 }, { order: 2, delayDays: 3 }];
    expect(buildTimeline(unsorted)).toEqual([
      { order: 1, day: 0, label: 'Day 0' }, { order: 2, day: 3, label: 'Day 3' }, { order: 3, day: 7, label: 'Day 7' }
    ]);
  });

  it('handles a single step', () => {
    expect(timelineSummary(steps(0))).toBe('Day 0');
  });

  it('says "No steps yet" for an empty list', () => {
    expect(buildTimeline([])).toEqual([]);
    expect(timelineSummary([])).toBe('No steps yet');
  });

  it('ignores the first step\'s delay (always Day 0)', () => {
    expect(timelineSummary(steps(5, 2))).toBe('Day 0 → Day 2');
  });

  it('summarises the window: weekdays collapse to a range', () => {
    expect(windowSummary('09:00', '17:00', SendDays.Weekdays, 'Asia/Dubai')).toBe('Mon–Fri, 09:00–17:00 (Asia/Dubai)');
  });

  it('summarises all days and odd sets', () => {
    expect(daysSummary(SendDays.All)).toBe('Every day');
    expect(daysSummary(SendDays.Tue | SendDays.Thu)).toBe('Tue, Thu');
    expect(daysSummary(SendDays.Sat | SendDays.Sun)).toBe('Sat, Sun');
    expect(daysSummary(SendDays.Mon | SendDays.Tue | SendDays.Wed | SendDays.Sat)).toBe('Mon–Wed, Sat');
    expect(daysSummary(SendDays.None)).toBe('No days');
  });

  it('labels a step\'s send time', () => {
    expect(stepSendLabel(0, '09:00', '17:00', SendDays.Weekdays)).toBe('Sends right away, between 09:00–17:00 Mon–Fri');
    expect(stepSendLabel(3, '09:00', '17:00', SendDays.Weekdays)).toBe('Sends on Day 3, between 09:00–17:00 Mon–Fri');
  });

  it('converts SendDays flags to and from day sets and the API\'s enum strings', () => {
    expect([...daysToSet(SendDays.Weekdays)]).toEqual([0, 1, 2, 3, 4]);
    expect(setToDays([1, 3])).toBe(SendDays.Tue | SendDays.Thu);
    expect(parseSendDays('Weekdays')).toBe(31);
    expect(parseSendDays('Mon, Wed')).toBe(5);
    expect(parseSendDays('All')).toBe(127);
    expect(parseSendDays(12)).toBe(12);
  });
});
