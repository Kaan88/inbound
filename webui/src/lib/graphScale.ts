import { graphCeiling } from './list';

/**
 * UI.B23: the speed graph's top with hysteresis, as the desktop graph does it. graphCeiling alone halved the scale the
 * moment a peak left the ten minutes shown, so the whole line leapt up. This grows at once when a figure needs more room,
 * but shrinks only after the figures have stayed well under the next lower step for a while, and then one step at a time.
 */
export class GraphScale {
  /** How long the figures must stay under the lower step before the scale takes it. */
  static readonly shrinkAfterMs = 5000;
  /** How long between one step down and the next. */
  static readonly stepEveryMs = 3000;
  /** The figures must be at most this share of what the lower step allows, so a peak near a step doesn't flap. */
  static readonly margin = 0.85;

  top = graphCeiling(0);
  private belowSince: number | null = null;

  /** Fits the scale at once (the server's history replaced this page's). */
  reset(peak: number): number {
    this.belowSince = null;
    this.top = graphCeiling(peak);
    return this.top;
  }

  /** The top for the figures' peak at `now` (milliseconds). */
  update(peak: number, now: number): number {
    const needed = graphCeiling(peak);
    if (needed >= this.top) {
      this.belowSince = null;
      this.top = needed;
      return this.top;
    }
    const lower = this.top / 2;
    // graphCeiling keeps the peak under the top by a tenth, so the lower step takes figures up to lower / 1.1.
    if (peak > (lower / 1.1) * GraphScale.margin) {
      this.belowSince = null;
      return this.top;
    }
    if (this.belowSince === null || now < this.belowSince) {
      this.belowSince = now;
      return this.top;
    }
    if (now - this.belowSince < GraphScale.shrinkAfterMs) return this.top;
    this.top = lower;
    this.belowSince = now - GraphScale.shrinkAfterMs + GraphScale.stepEveryMs;
    return this.top;
  }
}
