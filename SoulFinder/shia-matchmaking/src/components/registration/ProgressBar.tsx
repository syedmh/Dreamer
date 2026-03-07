import { STEP_TITLES } from "@/types/registration";
import { Progress } from "@/components/ui/progress";

interface ProgressBarProps {
  currentStep: number;
  onStepClick?: (step: number) => void;
}

export function ProgressBar({ currentStep, onStepClick }: ProgressBarProps) {
  const progressPercent = ((currentStep + 1) / STEP_TITLES.length) * 100;

  return (
    <div className="mb-8">
      <div className="flex items-center justify-between mb-2">
        <span className="text-sm font-medium text-gold">
          Step {currentStep + 1} of {STEP_TITLES.length}
        </span>
        <span className="text-sm text-muted-foreground">
          {STEP_TITLES[currentStep]}
        </span>
      </div>
      <Progress value={progressPercent} className="h-2 bg-border" />

      {/* Mobile: scrollable step pills */}
      <div className="mt-3 flex gap-2 overflow-x-auto pb-2 sm:hidden">
        {STEP_TITLES.map((title, index) => (
          <button
            key={title}
            onClick={() => onStepClick?.(index)}
            disabled={index > currentStep}
            className={`flex-shrink-0 rounded-full px-3 py-1 text-xs font-medium transition-colors ${
              index === currentStep
                ? "bg-gold text-black"
                : index < currentStep
                  ? "bg-primary-red text-white"
                  : "bg-border text-muted-foreground opacity-50"
            } ${index <= currentStep ? "cursor-pointer" : "cursor-not-allowed"}`}
          >
            {index + 1}. {title.split(" ")[0]}
          </button>
        ))}
      </div>

      {/* Desktop: full step indicators */}
      <div className="mt-4 hidden sm:flex justify-between">
        {STEP_TITLES.map((title, index) => (
          <button
            key={title}
            onClick={() => onStepClick?.(index)}
            disabled={index > currentStep}
            className={`flex flex-col items-center gap-1 transition-colors ${
              index <= currentStep
                ? "text-gold cursor-pointer"
                : "text-muted-foreground/50 cursor-not-allowed"
            }`}
          >
            <div
              className={`flex h-8 w-8 items-center justify-center rounded-full border text-xs font-medium ${
                index === currentStep
                  ? "border-gold bg-gold text-black"
                  : index < currentStep
                    ? "border-primary-red bg-primary-red text-white"
                    : "border-border text-muted-foreground"
              }`}
            >
              {index < currentStep ? "✓" : index + 1}
            </div>
            <span className="text-[10px] max-w-[80px] text-center leading-tight">
              {title}
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}
