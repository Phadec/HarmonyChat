import { Component, OnInit, Output, EventEmitter } from '@angular/core';
import { ChatService } from '../../services/chat.service';

@Component({
  selector: 'app-sidebar',
  templateUrl: './sidebar.component.html',
  styleUrls: ['./sidebar.component.css']
})
export class SidebarComponent implements OnInit {
  relationships: any[] = [];

  @Output() selectRecipient = new EventEmitter<string>();

  constructor(private chatService: ChatService) {}

  ngOnInit(): void {
    this.loadRelationships();
  }

  loadRelationships(): void {
    this.chatService.getRelationships().subscribe((response) => {
      this.relationships = response;
    });
  }

  onSelectRecipient(recipientId: string): void {
    this.selectRecipient.emit(recipientId);
  }
}
