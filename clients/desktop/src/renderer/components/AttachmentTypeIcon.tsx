import React from 'react';
import {
  FileArchive,
  FileAudio,
  FileCode2,
  FileJson,
  FileSpreadsheet,
  FileText,
  FileVideo,
  Image as ImageIcon,
  Presentation,
} from 'lucide-react';
import type { AttachmentCategory } from '../lib/attachments';

interface AttachmentTypeIconProps {
  category: AttachmentCategory;
  className?: string;
}

const CATEGORY_ICONS: Record<AttachmentCategory, React.ComponentType<{ className?: string }>> = {
  image: ImageIcon,
  pdf: FileText,
  word: FileText,
  spreadsheet: FileSpreadsheet,
  presentation: Presentation,
  json: FileJson,
  code: FileCode2,
  archive: FileArchive,
  audio: FileAudio,
  video: FileVideo,
  text: FileText,
  generic: FileText,
};

export const AttachmentTypeIcon: React.FC<AttachmentTypeIconProps> = ({
  category,
  className = 'h-4 w-4',
}) => {
  const Icon = CATEGORY_ICONS[category] ?? FileText;
  return <Icon className={className} />;
};
